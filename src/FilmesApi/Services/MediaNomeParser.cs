using System.Text.RegularExpressions;

namespace FilmesApi.Services;

/// <summary>
/// Deriva "isto é episódio de série?", "de qual série?" e "qual a ordem do episódio?"
/// só a partir do nome do arquivo/título — o modelo <see cref="Models.Filme"/> não guarda
/// nada de série/temporada. É a versão C# das mesmas regexes que <c>index.html</c> e
/// <c>feia.html</c> já usam pra agrupar a lista; manter os dois lados em sincronia.
/// </summary>
public static partial class MediaNomeParser
{
    // \d trocado por [0-9] de propósito: o JS casa só dígitos ASCII, o \d do .NET casaria
    // dígitos Unicode e divergiria em nomes exóticos.
    [GeneratedRegex(@"\bS([0-9]{1,2})[\s._-]*E([0-9]{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSxxExx();

    [GeneratedRegex(@"(?:^|[^0-9xX])([0-9]{1,2})x([0-9]{1,3})(?:[^0-9pP]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReNxNN();

    [GeneratedRegex(@"\b(?:epis[oó]dios?|episodes?|cap[ií]tulos?)\.?\s*([0-9]{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReEpNum();

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

    private static string PastaDe(string? arquivoPath)
    {
        if (string.IsNullOrEmpty(arquivoPath)) return "Sem pasta";
        var partes = arquivoPath.Split('/');
        return partes.Length > 1 ? string.Join('/', partes[..^1]) : "Sem pasta";
    }

    /// <summary>true se o nome do arquivo tem marcador de episódio (S01E02, 1x02, "Episódio 3").</summary>
    public static bool EhEpisodio(string? arquivoPath)
    {
        var n = NomeArquivo(arquivoPath);
        return ReSxxExx().IsMatch(n) || ReNxNN().IsMatch(n) || ReEpNum().IsMatch(n);
    }

    /// <summary>true se é trailer/sample/extra — não conta como episódio "de verdade".</summary>
    public static bool EhExtra(string? arquivoPath)
    {
        var n = NomeArquivo(arquivoPath);
        return ReExtra().IsMatch(n) || ReXbet().IsMatch(n);
    }

    /// <summary>Nome da série pra agrupar: a pasta, ou o prefixo do arquivo antes do marcador de episódio.</summary>
    public static string ChaveSerie(string? arquivoPath)
    {
        var pasta = PastaDe(arquivoPath);
        if (pasta != "Sem pasta") return pasta;

        var n = NomeArquivo(arquivoPath);
        var i = IndiceOuMenos1(ReSxxExx(), n);
        if (i < 0)
        {
            var j = IndiceOuMenos1(ReNxNN(), n);
            i = j <= 0 ? j : j + 1;  // ReNxNN come 1 char antes dos dígitos
        }
        if (i < 0) i = IndiceOuMenos1(ReEpNum(), n);

        var prefixo = i > 0 ? n[..i] : n;
        prefixo = RePontos().Replace(prefixo, " ").TrimEnd(' ', '-').Trim();
        return prefixo.Length > 0 ? prefixo : n;
    }

    /// <summary>Ordem do episódio dentro da série. (0, N) quando só há número solto ("Episódio N").
    /// null quando o nome não tem marcador nenhum.</summary>
    public static (int Temporada, int Episodio)? OrdemEpisodio(string titulo)
    {
        titulo ??= "";

        var m = ReSxxExx().Match(titulo);
        if (m.Success) return (ParseInt(m.Groups[1].Value), ParseInt(m.Groups[2].Value));

        m = ReNxNN().Match(titulo);
        if (m.Success) return (ParseInt(m.Groups[1].Value), ParseInt(m.Groups[2].Value));

        m = ReEpNum().Match(titulo);
        if (m.Success) return (0, ParseInt(m.Groups[1].Value));

        return null;
    }

    /// <summary>Título "limpo" pra busca de metadados: tira marcador de episódio, ano e ruído de release.
    /// Devolve também o ano se der pra achar.</summary>
    public static (string Titulo, int? Ano) TituloParaBusca(string? arquivoPath, string? tituloFallback = null)
    {
        var n = NomeArquivo(arquivoPath);
        if (string.IsNullOrWhiteSpace(n)) n = tituloFallback ?? "";

        // tira a extensão
        var ponto = n.LastIndexOf('.');
        if (ponto > 0 && n.Length - ponto <= 5) n = n[..ponto];

        n = RePontos().Replace(n, " ");

        // corta no marcador de episódio primeiro (antes do ano) — "Serie S03E01 2160p" não
        // deve virar título "Serie S03E01". ReNxNN "come 1 char antes", igual em ChaveSerie.
        var iSxx = IndiceOuMenos1(ReSxxExx(), n);
        if (iSxx > 0) n = n[..iSxx];
        var iNxx = IndiceOuMenos1(ReNxNN(), n);
        if (iNxx > 0) n = n[..(iNxx + 1)];
        var iEp = IndiceOuMenos1(ReEpNum(), n);
        if (iEp > 0) n = n[..iEp];

        int? ano = null;
        // Último ano da string (não o primeiro): "Blade Runner 2049 2017 1080p" -> 2017,
        // não 2049. E nunca aceita ano no começo ("1917 2019" -> ano 2019, título "1917").
        var anos = Regex.Matches(n, @"\b(19[0-9]{2}|20[0-9]{2})\b").Where(m => m.Index > 0).ToList();
        if (anos.Count > 0)
        {
            var ultimo = anos[^1];
            ano = ParseInt(ultimo.Value);
            n = n[..ultimo.Index];  // tudo depois do ano costuma ser release info
        }

        n = ReRuido().Replace(n, " ");
        n = Regex.Replace(n, @"[\[\]()_-]+", " ");
        n = Regex.Replace(n, @"\s+", " ").Trim();

        return (n, ano);
    }

    private static int IndiceOuMenos1(Regex re, string s)
    {
        var m = re.Match(s);
        return m.Success ? m.Index : -1;
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;
}
