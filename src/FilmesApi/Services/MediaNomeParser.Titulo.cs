using System.Text.RegularExpressions;

namespace FilmesApi.Services;

public static partial class MediaNomeParser
{
    [GeneratedRegex(@"E([0-9]{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex ReNumeroE();

    // "-07", "- E02", "-Ep.02" logo depois do marcador: ponta final de uma faixa ("[1x06-07]", "S01E01-02").
    [GeneratedRegex(@"^\s*[-–]\s*(?:E|Ep\.?)?\s*([0-9]{1,3})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReFimFaixa();

    /// <summary>Episódios que UM arquivo cobre: <c>S07E14E15</c>, <c>S01E01-E02</c>, <c>[1x06-07]</c>,
    /// <c>Episódio 01-02</c> → (temporada, 14, 15). Arquivo normal devolve de = até. Faixa de mais de 6 episódios é
    /// descartada (é o início de um título tipo "12 Monkeys", não uma faixa). <paramref name="temp"/>/<paramref name="ep"/>
    /// são os já classificados (<see cref="Classificar"/>) — não reclassifica.</summary>
    public static (int Temporada, int De, int Ate) FaixaDeEpisodios(string? arquivoPath, int temp, int ep)
    {
        var nome = SemExtensao(NomeArquivo(arquivoPath));
        var ate = ep;

        var m = ReSxxExx().Match(nome);
        if (m.Success)
        {
            foreach (Match n in ReNumeroE().Matches(m.Value)) ate = Math.Max(ate, ParseInt(n.Groups[1].Value));
            ate = Math.Max(ate, FimDeFaixa(nome, m.Index + m.Length));
        }
        else if ((m = ReNxNN().Match(nome)).Success)
            ate = Math.Max(ate, FimDeFaixa(nome, m.Groups[2].Index + m.Groups[2].Length));
        else if ((m = ReEpNum().Match(nome)).Success)
            ate = Math.Max(ate, FimDeFaixa(nome, m.Groups[1].Index + m.Groups[1].Length));

        return (temp, ep, ate > ep && ate - ep <= 6 ? ate : ep);
    }

    private static int FimDeFaixa(string nome, int pos)
    {
        var m = ReFimFaixa().Match(nome[pos..]);
        return m.Success ? ParseInt(m.Groups[1].Value) : 0;
    }

    // Domínio de site grudado no FIM do nome ("... - TORRENTMEGAFILMES.TV", "ONDEBAIXA.COM"). Só no
    // fim: no meio, "Flight.to.Remember" tem ".to" — que é TLD — e não é site nenhum.
    [GeneratedRegex(@"(?:\s*[-–—]\s*)?\b(?:www\.)?[a-z0-9][a-z0-9-]*\.(?:com|net|org|tv|to|se|la|xyz|info)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReDominioNoFim();

    /// <summary>O texto que vem DEPOIS do marcador de episódio no nome do arquivo (sem extensão), pela
    /// mesma ordem de sinais de <see cref="OrdemEpisodio"/>; null se não há marcador.</summary>
    private static string? TextoAposMarcador(string nome)
    {
        foreach (var re in new[] { ReSxxExx(), ReTempEpi(), ReNxNN(), ReEpNum(), ReEpPrefixo() })
        {
            var m = re.Match(nome);
            if (m.Success) return nome[(m.Index + m.Length)..];
        }
        return null;
    }

    /// <summary>Título do episódio a partir do nome do arquivo: o que sobra depois do marcador
    /// (<c>S01E05</c>, <c>1x05</c>, <c>Epi 05</c>, <c>05 - </c>) tirando propaganda de site, domínio no fim,
    /// assinatura de release e tudo da primeira marca de qualidade/codec pra frente
    /// (<see cref="ReRuido"/>: <c>1080p</c>, <c>x265</c>, <c>DVDRip</c>…). Palavra de título igual a uma dessas
    /// marcas ("Complete", "Dual") corta o título ali — fica sem título em vez de mostrar lixo.
    /// Null quando não sobra nada com cara de título.</summary>
    internal static string? TituloDoEpisodio(string nome)
    {
        var resto = TextoAposMarcador(nome);
        if (resto is null) return null;

        resto = ReAnuncioSite().Replace(resto, " ");
        resto = ReDominioNoFim().Replace(resto, "");
        var pareceRelease = resto.Contains('.') || resto.Contains('_');   // "Titulo.Do.Ep-GRUPO"
        resto = RePontos().Replace(resto, " ");
        var ruido = ReRuido().Match(resto);
        if (ruido.Success) resto = resto[..ruido.Index];
        resto = ReDominioSolto().Replace(resto, " ");
        if (pareceRelease) resto = ReAssinaturaRelease().Replace(resto.TrimEnd(), "");
        resto = Regex.Replace(resto, @"[\[\]()]+", " ");
        resto = Regex.Replace(resto, @"\s+", " ").Trim(' ', '-', '–', '—', '·', ':', '.', '_');

        // sobrou só o ano ("S01E02.2010.1080p") ou quase nada: não é título
        return resto.Length >= 2 && !Regex.IsMatch(resto, @"^(?:19|20)[0-9]{2}$") ? resto : null;
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
        // Domínio do uploader grudado no COMEÇO do nome do arquivo ("COMANDO.LA-Ainda.Estou...")
        // — mesmo problema do ReSiteNoComeco em ChaveSerie, mas aqui é o título do filme.
        // Antes de RePontos: o "." antes do TLD é o sinal que a regex procura.
        n = ReSiteNoComeco().Replace(n, "");
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
