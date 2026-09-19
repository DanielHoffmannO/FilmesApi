using System.Text;
using System.Text.RegularExpressions;

namespace FilmesApi.Services;

/// <summary>Tudo que dá pra deduzir de um filme só pelo caminho do arquivo — o modelo
/// <see cref="Models.Filme"/> não guarda nada de série/temporada.</summary>
/// <param name="EhEpisodio">É episódio de série?</param>
/// <param name="EhExtra">Trailer/sample/making-of — não entra na sequência de episódios.</param>
/// <param name="Serie">Nome da série pra exibir (só quando episódio) — mantém acento/maiúscula
/// de como a pasta foi nomeada.</param>
/// <param name="SerieChave">Chave de agrupamento de <see cref="Serie"/>, tolerante a
/// acento/maiúscula/espaço — pra unir temporadas cujas pastas vieram de fontes diferentes e
/// escreveram o nome ligeiramente diferente ("Diários de Um Vampiro" vs "Diarios de um
/// vampiro"). Use esta pra comparar/agrupar; <see cref="Serie"/> só pra exibir.</param>
/// <param name="Temporada">Temporada (0 quando o nome só tem número solto, ex.: "Capítulo 12").</param>
/// <param name="Episodio">Número do episódio.</param>
/// <param name="Rotulo">Como mostrar na lista: "S03E08" / "T1 Ep05" / "Ep 12 · título" / o título.</param>
/// <param name="Pasta">Pasta pai ("Sem pasta" na raiz de /media) — pro agrupamento "filme + extras".</param>
public record ClassificacaoMidia(
    bool EhEpisodio, bool EhExtra, string? Serie, string? SerieChave,
    int? Temporada, int? Episodio, string Rotulo, string Pasta);

/// <summary>
/// Fonte única da classificação série/filme/episódio. Antes essa lógica existia em
/// triplicata (aqui + nas telas); agora o servidor computa e as telas só renderizam.
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
    // ano, SxxExx (pasta de release com o episódio embutido no nome, tipo
    // "Severance.S02E05.1080p.WEB-DL.DUAL.5.1"), Sxx solto (só a temporada, sem episódio, tipo
    // "T.V.D.S07.WWW.TORRENTDOSFILMES.COM"), "completa/completo", " - ", "["}.
    [GeneratedRegex(@"\s*(?:[0-9]{1,2}\s*[ªº°]?\s*(?:a\s+)?(?:temporadas?|seasons?)|(?:temporadas?|seasons?)\s*[0-9]{1,2}|parte\s*[0-9]{1,2}|part\s*[0-9]{1,2}|\b[0-9]{1,2}\s*[ªº°]\b|\bS[0-9]{1,2}[\s._-]*E[0-9]{1,3}\b|\bS[0-9]{1,2}\b|19[0-9]{2}|20[0-9]{2}|completos?|completas?|complete|\s-\s|\[).*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReCorteSerie();

    // Último segmento da pasta COMEÇA com um marcador de temporada/arco (pode ter mais coisa
    // depois, tipo "Livro 1 - Água") -> subir pro segmento pai. Palavras completas só, sem $
    // no fim de propósito (senão "Livro 1 - Água" não bate).
    [GeneratedRegex(@"^\s*(?:season|temporada|livro|volume|disco?|parte|part|cd)\s*[0-9]{1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSegmentoTemporadaPrefixo();

    // Caso mais arriscado (letra solta "S"/"T") fica com match exato — "S1" sozinho é
    // claramente temporada, mas um segmento tipo "S1 Alguma Coisa Sem Relação" já não é.
    [GeneratedRegex(@"^\s*[ST]\s*[0-9]{1,2}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSegmentoTemporadaExata();

    [GeneratedRegex(@"\b(trailer|sample|amostra|promo|extras?|featurette|bastidores|deleted|nfo|readme)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReExtra();

    [GeneratedRegex(@"1xbet", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReXbet();

    // Tokens de qualidade/codec/origem que não fazem parte do nome da obra (pra busca no TMDB).
    [GeneratedRegex(@"\b(1080p|2160p|720p|480p|4k|uhd|hd|sd|bluray|blu-ray|bdrip|brrip|web-?dl|web-?rip|webrip|hdtv|dvdrip|remux|x264|x265|h ?264|h ?265|hevc|avc|aac|ac3|eac3|dts|ddp?5 ?1|10bit|hdr|dv|dolby|vision|dual|dublado|dery|legendado|nacional|multi|complete|prox)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReRuido();

    [GeneratedRegex(@"[._]+", RegexOptions.CultureInvariant)]
    private static partial Regex RePontos();

    // Propaganda de site dentro de colchete/parênteses ("[ACESSE COMANDOTORRENTS.COM]",
    // "(baixe em www.site.com)") — só remove quando tem palavra-gatilho ou domínio dentro,
    // nunca um colchete às cegas (um filme pode legitimamente ter "[Extended]" no nome).
    [GeneratedRegex(@"[\[(][^\[\]()]*(?:acesse|baix(?:e|ar)|download|www\.|\.(?:com|net|org|tv|to|se|xyz|info)\b)[^\[\]()]*[\])]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReAnuncioSite();

    // Assinatura de grupo de release bem no fim do nome ("-RICKSZ", "-STARCKFILMES").
    [GeneratedRegex(@"-[A-Z0-9]{3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReAssinaturaRelease();

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
            if (segs.Length > 1 && (ReSegmentoTemporadaPrefixo().IsMatch(baseSeg) || ReSegmentoTemporadaExata().IsMatch(baseSeg)))
                baseSeg = segs[^2];

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

    // Projeto roda com InvariantGlobalization=true (ver .csproj) — sem ICU, string.Normalize()
    // e CharUnicodeInfo não funcionam de forma confiável (passa no teste, que roda com ICU
    // completo no SDK, mas falha silenciosamente na imagem publicada). Por isso a troca de
    // acento é uma tabela ordinal explícita, igual ao resto do parser já faz.
    private static char SemAcentoMinuscula(char c) => c switch
    {
        'á' or 'à' or 'â' or 'ã' or 'ä' or 'Á' or 'À' or 'Â' or 'Ã' or 'Ä' => 'a',
        'é' or 'è' or 'ê' or 'ë' or 'É' or 'È' or 'Ê' or 'Ë' => 'e',
        'í' or 'ì' or 'î' or 'ï' or 'Í' or 'Ì' or 'Î' or 'Ï' => 'i',
        'ó' or 'ò' or 'ô' or 'õ' or 'ö' or 'Ó' or 'Ò' or 'Ô' or 'Õ' or 'Ö' => 'o',
        'ú' or 'ù' or 'û' or 'ü' or 'Ú' or 'Ù' or 'Û' or 'Ü' => 'u',
        'ç' or 'Ç' => 'c',
        'ñ' or 'Ñ' => 'n',
        'ý' or 'Ý' or 'ÿ' => 'y',
        >= 'A' and <= 'Z' => (char)(c + 32),
        _ => c,
    };

    // Apelido conhecido -> chave canônica. Acento/maiúscula sozinhos não resolvem "The Vampire
    // Diaries" == "Diários de Um Vampiro" == "T V D" (temporadas da mesma série vieram de 3
    // fontes com nome em idioma/abreviação diferentes) — são palavras diferentes, não só
    // grafia diferente. Lista pequena e explícita, cresce sob demanda quando aparecer outro
    // caso assim na prática; as chaves aqui já estão no formato pós-normalização (minúsculo,
    // sem acento).
    private static readonly Dictionary<string, string> ApelidosSerie = new()
    {
        ["the vampire diaries"] = "diarios de um vampiro",
        ["t v d"] = "diarios de um vampiro",
    };

    /// <summary>Chave de agrupamento tolerante a acento/maiúscula/espaço — mesma série
    /// escrita de jeitos ligeiramente diferentes em pastas de fontes diferentes ("Diários de
    /// Um Vampiro" / "Diarios de um vampiro" / "Diários de um Vampiro") cai na mesma chave.
    /// Também resolve apelidos conhecidos (<see cref="ApelidosSerie"/>). Só serve pra
    /// comparar/agrupar — quem exibe usa <see cref="ChaveSerie"/> sem alterar.</summary>
    public static string? ChaveAgrupamento(string? serie)
    {
        if (string.IsNullOrEmpty(serie)) return serie;

        var sb = new StringBuilder(serie.Length);
        foreach (var c in serie) sb.Append(SemAcentoMinuscula(c));

        var chave = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return ApelidosSerie.GetValueOrDefault(chave, chave);
    }

    /// <summary>Nome de exibição de uma pasta "filme + extras" (trailer/sample junto) — a
    /// pasta pode estar em qualquer profundidade ("extra/Nome Feio"), só o último segmento
    /// importa pra exibir. Tira ponto/underscore, propaganda de site entre colchetes,
    /// qualidade/codec (<see cref="ReRuido"/>) e assinatura de release no fim. Só pra
    /// exibir — o agrupamento continua pelo caminho relativo completo (<c>Pasta</c> bruto),
    /// que já é único por pasta real.</summary>
    public static string NomePastaExibicao(string pasta)
    {
        var i = pasta.LastIndexOf('/');
        var basename = i >= 0 ? pasta[(i + 1)..] : pasta;

        // ReAssinaturaRelease só entra se a pasta já tem outro indício de nome de release
        // (ponto/underscore separando tokens, ou tag de qualidade/codec) -- sem isso, um nome
        // limpo tipo "X-MEN" ou "K-PAX" (comuns de verdade) teria o final maiúsculo cortado
        // igual a uma assinatura de grupo de release, virando "X"/"K" na tela.
        var pareceRelease = basename.Contains('.') || basename.Contains('_') || ReRuido().IsMatch(basename);

        var nome = RePontos().Replace(basename, " ");
        nome = ReAnuncioSite().Replace(nome, " ");
        nome = ReRuido().Replace(nome, " ");
        if (pareceRelease) nome = ReAssinaturaRelease().Replace(nome, "");
        nome = Regex.Replace(nome, @"[\[\]()]+", " ");
        nome = Regex.Replace(nome, @"\s+", " ").Trim(' ', '-', '–', '—');

        return nome.Length > 0 ? nome : basename;
    }

    /// <summary>Classificação completa — o que o <c>FilmeResponse</c> entrega pras telas.</summary>
    public static ClassificacaoMidia Classificar(string? arquivoPath, string titulo)
    {
        var pasta = PastaDe(arquivoPath);
        var ordem = OrdemEpisodio(arquivoPath);
        if (ordem is not (int temp, int ep))
            return new ClassificacaoMidia(false, EhExtra(arquivoPath), null, null, null, null, titulo, pasta);

        var serie = ChaveSerie(arquivoPath);
        return new ClassificacaoMidia(
            EhEpisodio: true,
            EhExtra: EhExtra(arquivoPath),
            Serie: serie,
            SerieChave: ChaveAgrupamento(serie),
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
        => TituloParaBusca(arquivoPath, tituloFallback, permiteFallbackSerie: true);

    // permiteFallbackSerie=false na segunda chamada: sem isso, uma pasta cujo nome é só ruído
    // de release ("Legendado", "1080p", "Dual"...) faz ChaveSerie devolver o MESMO texto de
    // entrada (sem "/" pra extrair segmento de pasta), e a chamada recursiva reentra com
    // argumento idêntico — StackOverflowException garantido (não capturável em .NET, derruba
    // o processo inteiro no meio de um /scan). Com o guard, no máximo 1 nível de recursão,
    // só pra tentar achar um ano no nome da série.
    private static (string Titulo, int? Ano) TituloParaBusca(string? arquivoPath, string? tituloFallback, bool permiteFallbackSerie)
    {
        var n = SemExtensao(NomeArquivo(arquivoPath));
        if (string.IsNullOrWhiteSpace(n)) n = tituloFallback ?? "";
        n = RePontos().Replace(n, " ");

        // "8 - I See You" -> "I See You": num de episódio no começo é ruído aqui
        // (já está em Temporada/Episódio). Só quando é mesmo episódio.
        if (OrdemEpisodio(arquivoPath) is not null)
            n = Regex.Replace(n, @"^\s*\[?\s*\d{1,3}\s*\]?\s*[-–—.):]*\s*", "");

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
        if (permiteFallbackSerie && (n.Length <= 2 || Regex.IsMatch(n, @"^[0-9]{1,3}$")))
        {
            var serie = ChaveSerie(arquivoPath);
            if (serie.Length > 2 && serie != n)
                return (serie, ano ?? TituloParaBusca(serie, null, permiteFallbackSerie: false).Ano);
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
