using System.Text.RegularExpressions;

namespace FilmesApi.Services;

/// <summary>Tudo que dá pra deduzir de um filme só pelo caminho do arquivo — o modelo
/// <see cref="Models.Filme"/> não guarda nada de série/temporada.</summary>
/// <param name="EhEpisodio">É episódio de série?</param>
/// <param name="EhExtra">Trailer/sample/making-of — não entra na sequência de episódios.</param>
/// <param name="Serie">Nome da série pra agrupar (só quando episódio).</param>
/// <param name="Temporada">Temporada (0 quando o nome só tem número solto, ex.: "Capítulo 12").</param>
/// <param name="Episodio">Número do episódio.</param>
/// <param name="Rotulo">Como mostrar na lista: "S03E08" / "T1 Ep05" / "Ep 12 · título" / o título.</param>
/// <param name="Pasta">Pasta pai ("Sem pasta" na raiz de /media) — pro agrupamento "filme + extras".</param>
public record ClassificacaoMidia(
    bool EhEpisodio, bool EhExtra, string? Serie,
    int? Temporada, int? Episodio, string Rotulo, string Pasta);

/// <summary>
/// Fonte única da classificação série/filme/episódio. Antes essa lógica existia em
/// triplicata (aqui + index.html + feia.html); agora o servidor computa e as telas só renderizam.
/// </summary>
public static partial class MediaNomeParser
{
    // \d -> [0-9] de propósito: casa só dígito ASCII (o \d do .NET casaria dígito Unicode).
    [GeneratedRegex(@"\bS([0-9]{1,2})[\s._-]*E([0-9]{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSxxExx();

    [GeneratedRegex(@"(?:^|[^0-9xX])([0-9]{1,2})x([0-9]{1,3})(?:[^0-9pP]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReNxNN();

    [GeneratedRegex(@"\b(?:epis[oó]dios?|episodes?|cap[ií]tulos?)\.?\s*([0-9]{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReEpNum();

    // Número solto no começo do nome do arquivo: "8 - I See You", "08. Título", "E08 - x",
    // "[12] Título" (colchete já é delimitador — não precisa de separador depois). Fora do
    // colchete, exige separador logo após o número, pra não pegar "1917" nem "2001 A Space
    // Odyssey". Só é usado quando a PASTA tem marcador de temporada.
    [GeneratedRegex(@"^\s*(?:\[\s*([0-9]{1,3})\s*\]|(?:e|ep|epis[oó]dio|cap[ií]tulo)?\s*([0-9]{1,3})\s*[-–—.):]\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReEpPrefixo();

    // Temporada indicada só na pasta: "3 Temporada", "3ª Temporada", "Temporada 3",
    // "Season 3", "S03", "T3".
    [GeneratedRegex(@"([0-9]{1,2})\s*[ªº°]?\s*(?:a\s+)?(?:temporadas?|seasons?)\b|(?:temporadas?|seasons?)\s*([0-9]{1,2})\b|\b[ST]\s*0*([0-9]{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReTemporadaPasta();

    // Onde cortar o nome da pasta pra virar o nome da série: no 1º de {temporada, parte N,
    // ano, "completa/completo", " - ", "["}.
    [GeneratedRegex(@"\s*(?:[0-9]{1,2}\s*[ªº°]?\s*(?:a\s+)?(?:temporadas?|seasons?)|(?:temporadas?|seasons?)\s*[0-9]{1,2}|parte\s*[0-9]{1,2}|part\s*[0-9]{1,2}|\b[0-9]{1,2}\s*[ªº°]\b|19[0-9]{2}|20[0-9]{2}|completos?|completas?|complete|\s-\s|\[).*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReCorteSerie();

    // Último segmento da pasta é "só temporada" -> subir pro segmento pai.
    [GeneratedRegex(@"^\s*(?:season|temporada|s|t|disco?|parte|part|cd)\s*[0-9]{1,2}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSegmentoTemporada();

    [GeneratedRegex(@"\b(trailer|sample|amostra|promo|extras?|featurette|bastidores|deleted|nfo|readme)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReExtra();

    [GeneratedRegex(@"1xbet", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReXbet();

    // Tokens de qualidade/codec/origem que não fazem parte do nome da obra (pra busca no TMDB).
    [GeneratedRegex(@"\b(1080p|2160p|720p|480p|4k|uhd|hd|sd|bluray|blu-ray|bdrip|brrip|web-?dl|web-?rip|webrip|hdtv|dvdrip|remux|x264|x265|h ?264|h ?265|hevc|avc|aac|ac3|eac3|dts|ddp?5 ?1|10bit|hdr|dv|dolby|vision|dual|dublado|dery|legendado|nacional|multi|complete|prox)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReRuido();

    [GeneratedRegex(@"[._]+", RegexOptions.CultureInvariant)]
    private static partial Regex RePontos();

    private static string NomeArquivo(string? path) =>
        string.IsNullOrEmpty(path) ? "" : Regex.Replace(path, @"^.*/", "");

    private static string SemExtensao(string nome)
    {
        var ponto = nome.LastIndexOf('.');
        return ponto > 0 && nome.Length - ponto <= 5 ? nome[..ponto] : nome;
    }

    private static string[] SegmentosPasta(string? arquivoPath) =>
        string.IsNullOrEmpty(arquivoPath) ? [] : arquivoPath.Split('/')[..^1];

    private static string PastaDe(string? arquivoPath)
    {
        var s = SegmentosPasta(arquivoPath);
        return s.Length > 0 ? string.Join('/', s) : "Sem pasta";
    }

    /// <summary>Temporada indicada pela pasta (qualquer segmento), ou null.</summary>
    private static int? TemporadaDaPasta(string? arquivoPath)
    {
        foreach (var seg in SegmentosPasta(arquivoPath))
        {
            var m = ReTemporadaPasta().Match(seg);
            if (m.Success)
                foreach (var g in m.Groups.Values.Skip(1))
                    if (g.Success && int.TryParse(g.Value, out var v) && v > 0) return v;
        }
        return null;
    }

    // ─── API pública ────────────────────────────────────────────────────

    public static bool EhExtra(string? arquivoPath)
    {
        var n = NomeArquivo(arquivoPath);
        return ReExtra().IsMatch(n) || ReXbet().IsMatch(n);
    }

    public static bool EhEpisodio(string? arquivoPath) => OrdemEpisodio(arquivoPath) is not null;

    /// <summary>(temporada, episódio) do arquivo, ou null se não é episódio. Ordem de sinal:
    /// SxxExx / NxNN no nome &gt; "Episódio N" no nome &gt; número solto no nome QUANDO a pasta
    /// diz a temporada (senão seria falso-positivo com pasta de filmes numerados).</summary>
    public static (int Temporada, int Episodio)? OrdemEpisodio(string? arquivoPath)
    {
        var nome = SemExtensao(NomeArquivo(arquivoPath));

        var m = ReSxxExx().Match(nome);
        if (m.Success) return (ParseInt(m.Groups[1].Value), ParseInt(m.Groups[2].Value));

        m = ReNxNN().Match(nome);
        if (m.Success) return (ParseInt(m.Groups[1].Value), ParseInt(m.Groups[2].Value));

        m = ReEpNum().Match(nome);
        if (m.Success) return (TemporadaDaPasta(arquivoPath) ?? 0, ParseInt(m.Groups[1].Value));

        var tempPasta = TemporadaDaPasta(arquivoPath);
        if (tempPasta is int t)
        {
            m = ReEpPrefixo().Match(nome);
            if (m.Success)
            {
                var g = m.Groups[1].Success ? m.Groups[1] : m.Groups[2];  // [1]=colchete, [2]=solto
                return (t, ParseInt(g.Value));
            }
        }
        return null;
    }

    /// <summary>Nome da série pra agrupar. Pasta normalizada (tira "N Temporada", "Parte N",
    /// ano, "[dominio]", " - grupo"), ou — quando o arquivo está solto — o prefixo do nome.</summary>
    public static string ChaveSerie(string? arquivoPath)
    {
        var segs = SegmentosPasta(arquivoPath);
        if (segs.Length > 0)
        {
            // "Serie/Season 1/ep.mkv" -> usa "Serie"
            var baseSeg = segs[^1];
            if (segs.Length > 1 && ReSegmentoTemporada().IsMatch(baseSeg)) baseSeg = segs[^2];

            var nome = RePontos().Replace(baseSeg, " ");
            nome = ReCorteSerie().Replace(nome, "").Trim(' ', '-', '–', '—');
            if (nome.Length > 0) return nome;
        }

        // arquivo solto na raiz: prefixo antes do marcador de episódio
        var n = NomeArquivo(arquivoPath);
        var i = IndiceOuMenos1(ReSxxExx(), n);
        if (i < 0) { var j = IndiceOuMenos1(ReNxNN(), n); i = j <= 0 ? j : j + 1; }
        if (i < 0) i = IndiceOuMenos1(ReEpNum(), n);
        var prefixo = i > 0 ? n[..i] : n;
        prefixo = RePontos().Replace(prefixo, " ").TrimEnd(' ', '-').Trim();
        return prefixo.Length > 0 ? prefixo : n;
    }

    /// <summary>Classificação completa — o que o <c>FilmeResponse</c> entrega pras telas.</summary>
    public static ClassificacaoMidia Classificar(string? arquivoPath, string titulo)
    {
        var pasta = PastaDe(arquivoPath);
        var ordem = OrdemEpisodio(arquivoPath);
        if (ordem is not (int temp, int ep))
            return new ClassificacaoMidia(false, EhExtra(arquivoPath), null, null, null, titulo, pasta);

        return new ClassificacaoMidia(
            EhEpisodio: true,
            EhExtra: EhExtra(arquivoPath),
            Serie: ChaveSerie(arquivoPath),
            Temporada: temp,
            Episodio: ep,
            Rotulo: MontarRotulo(SemExtensao(NomeArquivo(arquivoPath)), temp, ep),
            Pasta: pasta);
    }

    private static string MontarRotulo(string nome, int temp, int ep)
    {
        if (temp > 0) return $"S{temp:00}E{ep:00}";

        // "Episódio 12 - Título" / "12 - Título" -> "Ep 12 · Título"
        var m = ReEpNum().Match(nome);
        var resto = m.Success ? nome[(m.Index + m.Length)..] : "";
        if (!m.Success) { var p = ReEpPrefixo().Match(nome); if (p.Success) resto = nome[(p.Index + p.Length)..]; }
        resto = resto.TrimStart(' ', '.', '_', '·', ':', '–', '—', '-').TrimEnd();
        return resto.Length > 0 ? $"Ep {ep:00} · {resto}" : $"Ep {ep:00}";
    }

    /// <summary>Título "limpo" pra busca de metadados: tira marcador de episódio, ano e ruído
    /// de release. Pra episódio cujo nome de arquivo é só "N - Título", usa o nome da série.</summary>
    public static (string Titulo, int? Ano) TituloParaBusca(string? arquivoPath, string? tituloFallback = null)
    {
        var n = SemExtensao(NomeArquivo(arquivoPath));
        if (string.IsNullOrWhiteSpace(n)) n = tituloFallback ?? "";
        n = RePontos().Replace(n, " ");

        // corta no marcador de episódio (antes do ano). ReNxNN "come 1 char antes".
        var iSxx = IndiceOuMenos1(ReSxxExx(), n);
        if (iSxx > 0) n = n[..iSxx];
        var iNxx = IndiceOuMenos1(ReNxNN(), n);
        if (iNxx > 0) n = n[..(iNxx + 1)];
        var iEp = IndiceOuMenos1(ReEpNum(), n);
        if (iEp > 0) n = n[..iEp];

        int? ano = null;
        var anos = Regex.Matches(n, @"\b(19[0-9]{2}|20[0-9]{2})\b").Where(m => m.Index > 0).ToList();
        if (anos.Count > 0)
        {
            ano = ParseInt(anos[^1].Value);
            n = n[..anos[^1].Index];
        }

        n = ReRuido().Replace(n, " ");
        n = Regex.Replace(n, @"[\[\]()_-]+", " ");
        n = Regex.Replace(n, @"\s+", " ").Trim();

        // Nome de arquivo era só o número/título do episódio -> busca pelo nome da série.
        if (n.Length <= 2 || Regex.IsMatch(n, @"^[0-9]{1,3}$"))
        {
            var serie = ChaveSerie(arquivoPath);
            if (serie.Length > 2) return (serie, ano ?? TituloParaBusca(serie).Ano);
        }
        return (n, ano);
    }

    private static int IndiceOuMenos1(Regex re, string s)
    {
        var m = re.Match(s);
        return m.Success ? m.Index : -1;
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;
}
