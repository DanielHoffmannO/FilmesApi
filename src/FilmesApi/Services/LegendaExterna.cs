using System.Text;

namespace FilmesApi.Services;

/// <summary>Legenda "solta" (.srt/.vtt/.ass/.ssa) que veio junto do vídeo, ao lado dele ou
/// numa subpasta <c>Legendas/</c>. <c>Titulo</c> é o rótulo que aparece no menu de CC.</summary>
internal record LegendaExterna(string Caminho, string? Idioma, string Titulo, bool Forced);

/// <summary>
/// Acha as legendas externas de um vídeo. Rip de série/filme costuma trazer o .srt como
/// arquivo separado (<c>01 - Pilot.mp4</c> + <c>Legendas/01 - Pilot.srt</c>) em vez de embutido
/// — sem isto a API só enxergava faixa embutida e o menu de legenda ficava vazio.
///
/// Regras, nesta ordem:
/// <list type="number">
/// <item>candidatas = legendas na pasta do vídeo + em subpastas com cara de legenda
/// (<c>Legendas</c>, <c>Legenda Forçada</c>, <c>Subs</c>, <c>eng_subs</c>…), 1 nível só;</item>
/// <item>casa por nome (<see cref="Casa"/>): igual ignorando acento/pontuação, "nome.por.srt",
/// ou mesmo SxxEyy / 1x01;</item>
/// <item>se nada casou e o vídeo é o ÚNICO da pasta (filme com legenda de nome diferente),
/// usa as candidatas — com teto pra não varrer uma pasta-lixeira.</item>
/// </list>
/// </summary>
internal static class LegendaExternaLocalizador
{
    private static readonly string[] ExtLegenda = [".srt", ".vtt", ".ass", ".ssa"];
    private static readonly string[] ExtVideo = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v"];
    private const int MaxLegendas = 12;
    private const int MaxFallbackSemNome = 6;

    // Só tokens que não se confundem com palavra de título ("it", "de", "es", "br"… ficam de
    // fora: "Run for it.srt" não é legenda italiana).
    private static readonly Dictionary<string, string> TokenIdioma = new(StringComparer.Ordinal)
    {
        ["por"] = "por",
        ["pob"] = "por",
        ["pt"] = "por",
        ["ptbr"] = "por",
        ["brazilian"] = "por",
        ["portuguese"] = "por",
        ["portugues"] = "por",
        ["eng"] = "eng",
        ["en"] = "eng",
        ["english"] = "eng",
        ["ingles"] = "eng",
        ["spa"] = "spa",
        ["spanish"] = "spa",
        ["espanhol"] = "spa",
        ["fre"] = "fra",
        ["fra"] = "fra",
        ["french"] = "fra",
        ["frances"] = "fra",
        ["ger"] = "deu",
        ["deu"] = "deu",
        ["german"] = "deu",
        ["alemao"] = "deu",
        ["ita"] = "ita",
        ["italian"] = "ita",
        ["italiano"] = "ita",
        ["jpn"] = "jpn",
        ["japanese"] = "jpn",
        ["japones"] = "jpn",
    };

    private static readonly Dictionary<string, string> NomeIdioma = new()
    {
        ["por"] = "Português",
        ["eng"] = "Inglês",
        ["spa"] = "Espanhol",
        ["fra"] = "Francês",
        ["deu"] = "Alemão",
        ["ita"] = "Italiano",
        ["jpn"] = "Japonês",
    };

    static LegendaExternaLocalizador()
    {
        // Legenda de rip brasileiro é quase sempre Windows-1252 (ISO-8859-1 + aspas curvas);
        // sem o provider o .NET só conhece UTF-8/UTF-16/Latin1.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Legendas externas do vídeo, em ordem estável (o índice de cada uma depende
    /// disso: ver <see cref="SubtitleService"/>). Lista vazia se não houver ou a pasta não abrir.</summary>
    internal static IReadOnlyList<LegendaExterna> Localizar(string videoAbsoluto)
    {
        try
        {
            var dir = Path.GetDirectoryName(videoAbsoluto);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];

            var stemVideo = Path.GetFileNameWithoutExtension(videoAbsoluto);
            var candidatas = new List<(string Caminho, string Pasta)>();

            foreach (var f in Directory.EnumerateFiles(dir))
                if (EhLegenda(f)) candidatas.Add((f, ""));

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var nome = Path.GetFileName(sub);
                if (!EhPastaDeLegenda(nome)) continue;
                foreach (var f in Directory.EnumerateFiles(sub))
                    if (EhLegenda(f)) candidatas.Add((f, nome));
            }

            var escolhidas = candidatas
                .Where(c => Casa(stemVideo, Path.GetFileNameWithoutExtension(c.Caminho)))
                .ToList();

            if (escolhidas.Count == 0 && candidatas.Count is > 0 and <= MaxFallbackSemNome)
            {
                var videosNaPasta = Directory.EnumerateFiles(dir)
                    .Count(f => ExtVideo.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
                if (videosNaPasta == 1) escolhidas = candidatas;
            }

            var ordenadas = escolhidas
                .OrderBy(c => c.Caminho, StringComparer.Ordinal)
                .Take(MaxLegendas)
                .Select(c => Montar(stemVideo, c.Caminho, c.Pasta))
                .ToList();
            return DesambiguarTitulos(ordenadas);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Link simbólico pode apontar pra fora da pasta de mídia (ou pra FIFO/dispositivo, que
    // travaria a leitura); arquivo vazio não tem o que mostrar. Só arquivo comum entra.
    internal static bool EhLegenda(string caminho)
    {
        if (!ExtLegenda.Contains(Path.GetExtension(caminho), StringComparer.OrdinalIgnoreCase)) return false;
        var fi = new FileInfo(caminho);
        return fi.LinkTarget is null && fi.Length > 0;
    }

    internal static bool EhPastaDeLegenda(string nome)
    {
        var n = Normalizar(nome);
        return n.StartsWith("legenda", StringComparison.Ordinal)
            || n.StartsWith("subtitle", StringComparison.Ordinal)
            || n.StartsWith("subs", StringComparison.Ordinal)
            || n == "sub"
            || n.EndsWith("subs", StringComparison.Ordinal);
    }

    /// <summary>Minúsculas, sem acento, tudo que não é letra/dígito vira espaço único —
    /// "23 -Hawaii" e "23 - Hawaii" ficam iguais, "Legenda Forçada" vira "legenda forcada".</summary>
    internal static string Normalizar(string s)
    {
        var sb = new StringBuilder(s.Length);
        var ultimoEspaco = true;
        foreach (var original in s)
        {
            // Não usar string.Normalize(FormD): sem ICU (InvariantGlobalization) não decompõe nada.
            var c = MediaNomeParser.SemAcentoMinuscula(original);
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); ultimoEspaco = false; }
            else if (!ultimoEspaco) { sb.Append(' '); ultimoEspaco = true; }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A legenda <paramref name="legendaStem"/> é deste vídeo? Compara os nomes sem
    /// extensão.</summary>
    internal static bool Casa(string videoStem, string legendaStem)
    {
        var nv = Normalizar(videoStem);
        var ns = Normalizar(legendaStem);
        if (nv.Length == 0 || ns.Length == 0) return false;
        if (ns == nv) return true;
        // "Filme.pt-BR.srt" casa com "Filme"; "Alien 3.srt" NÃO casa com "Alien" — só vale o
        // que sobra do nome se for marca de idioma/forçada.
        if (ns.StartsWith(nv + " ", StringComparison.Ordinal) && SoMarcaDeIdioma(ns[nv.Length..])) return true;

        var kv = ChaveEpisodio(videoStem);
        return kv is not null && kv == ChaveEpisodio(legendaStem);
    }

    private static readonly HashSet<string> TokensForcada = new(StringComparer.Ordinal)
        { "forced", "forcada", "forcado", "forcadas", "sdh", "cc" };

    private static bool SoMarcaDeIdioma(string resto) =>
        resto.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(t => t == "br" || TokenIdioma.ContainsKey(t) || TokensForcada.Contains(t));

    /// <summary>(temporada, episódio) pelo MESMO parser do catálogo — assim "S01.E02", "1x6" etc.
    /// pareiam igual ao que a tela classifica como episódio. A extensão postiça evita que
    /// o parser trate o fim do nome ("...S01.E02") como extensão de arquivo.</summary>
    internal static (int Temporada, int Episodio)? ChaveEpisodio(string stem) =>
        MediaNomeParser.OrdemEpisodio(stem + ".x");

    /// <summary>Idioma ("por", "eng"…) lido do que sobra do nome depois do nome do vídeo
    /// ("Filme.pt-BR.srt" → por) e do nome da subpasta ("eng_subs" → eng). Null se não dá
    /// pra saber — melhor sem idioma do que chutar errado.</summary>
    internal static string? IdiomaDoNome(string videoStem, string legendaStem, string pasta)
    {
        var nv = Normalizar(videoStem);
        var ns = Normalizar(legendaStem);
        var sufixo = ns.StartsWith(nv + " ", StringComparison.Ordinal) ? ns[nv.Length..] : "";
        var tokens = sufixo.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(Normalizar(pasta).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var t in tokens)
            if (TokenIdioma.TryGetValue(t, out var idioma)) return idioma;
        return null;
    }

    /// <summary>Lê o texto da legenda como UTF-8 se for válido, senão Windows-1252. Respeita
    /// BOM (UTF-8/UTF-16) e remove ele. Sem isto "ação" vira "a��o" — ffmpeg e o
    /// navegador assumem UTF-8.</summary>
    internal static string DecodificarTexto(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(bytes); }
    }

    private static LegendaExterna Montar(string videoStem, string caminho, string pasta)
    {
        var stem = Path.GetFileNameWithoutExtension(caminho);
        var norm = Normalizar(stem + " " + pasta);
        var forced = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(TokensForcada.Contains);
        var idioma = IdiomaDoNome(videoStem, stem, pasta);

        string titulo;
        if (idioma is not null) titulo = NomeIdioma[idioma];
        else if (Casa(videoStem, stem)) titulo = "Legenda";
        else titulo = stem;   // "Comentários do Diretor" e afins: o nome já diz o que é

        if (forced) titulo += " (forçada)";
        return new LegendaExterna(caminho, idioma, titulo, forced);
    }

    /// <summary>Dois "Português" no mesmo menu não dá pra distinguir — numera os repetidos.</summary>
    private static List<LegendaExterna> DesambiguarTitulos(List<LegendaExterna> lista)
    {
        var vistos = new Dictionary<string, int>(StringComparer.Ordinal);
        var resultado = new List<LegendaExterna>(lista.Count);
        foreach (var l in lista)
        {
            vistos[l.Titulo] = vistos.GetValueOrDefault(l.Titulo) + 1;
            resultado.Add(vistos[l.Titulo] == 1 ? l : l with { Titulo = $"{l.Titulo} {vistos[l.Titulo]}" });
        }
        return resultado;
    }
}
