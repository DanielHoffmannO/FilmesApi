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
    // "(?:E[0-9]{1,3})*" no fim: episódio combinado tipo "S07E14E15" (2 episódios grudados
    // num arquivo só) — usa o PRIMEIRO número (14) como representante, só pra não sobrar
    // como "filme" solto por não bater SxxExx nenhum.
    [GeneratedRegex(@"\bS([0-9]{1,2})[\s._-]*E([0-9]{1,3})(?:E[0-9]{1,3})*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSxxExx();

    [GeneratedRegex(@"(?:^|[^0-9xX])([0-9]{1,2})x([0-9]{1,3})(?:[^0-9pP]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReNxNN();

    [GeneratedRegex(@"\b(?:epis[oó]dios?|episodes?|cap[ií]tulos?)\.?\s*([0-9]{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReEpNum();

    // "Temp 01 - Epi 03 - Título.mkv" / "Temp 05p1 - Epi 08 - Título.mkv" (a parte "p1"/"p2"
    // de release dividida em 2 é ignorada — mesma temporada, sem token de temporada/episódio
    // por extenso nem abreviação "S/E", mas inequívoco (mesmo padrão em todo o catálogo:
    // Hora de Aventura T1-5, Apenas um Show T1). Prioridade alta: roda antes até de olhar a
    // pasta, é auto-suficiente com temporada E episódio no próprio nome do arquivo.
    [GeneratedRegex(@"\bTemp\.?\s*([0-9]{1,2})(?:p[0-9])?\s*[-–—.]\s*Epi\.?\s*([0-9]{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReTempEpi();

    // Número solto no começo do nome do arquivo: "8 - I See You", "08. Título", "E08 - x",
    // "[12] Título" (colchete já é delimitador — não precisa de separador depois). Fora do
    // colchete, exige separador logo após o número, pra não pegar "1917" nem "2001 A Space
    // Odyssey". "(?:E[0-9]{1,3})*" antes do separador: episódio combinado sem "S" na frente
    // ("13E14E15E16 - Venha Comigo" — 4 episódios grudados, usa 13 como representante). Só é
    // usado quando a PASTA tem marcador de temporada.
    [GeneratedRegex(@"^\s*(?:\[\s*([0-9]{1,3})\s*\]|(?:e|ep|epis[oó]dio|cap[ií]tulo)?\s*([0-9]{1,3})(?:E[0-9]{1,3})*\s*[-–—.):]\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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

    // Nome de arquivo que é SÓ o domínio do site que fez o upload ("COMANDOTORRENTS.COM.mp4",
    // "TorrentDosFilmes.SE.mp4") — não é o filme, é propaganda solta que o uploader incluiu
    // junto (o real costuma vir como .url/.png, mas às vezes vem com extensão de vídeo de
    // verdade e passa pelo scan como se fosse um filme a mais na pasta).
    [GeneratedRegex(@"^[a-z0-9-]+\.(?:com|net|org|to|se|la|xyz|info)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReNomeEhSoDominio();

    // Tokens de qualidade/codec/origem que não fazem parte do nome da obra (pra busca no TMDB).
    [GeneratedRegex(@"\b(1080p|2160p|720p|480p|4k|uhd|hd|sd|bluray|blu-ray|bdrip|brrip|web-?dl|web-?rip|webrip|hdtv|dvdrip|remux|x264|x265|h ?264|h ?265|hevc|avc|aac|ac3|eac3|dts|ddp?5 ?1|10bit|hdr|dv|dolby|vision|dual|dublado|dery|legendado|nacional|multi|complete|prox)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReRuido();

    [GeneratedRegex(@"[._]+", RegexOptions.CultureInvariant)]
    private static partial Regex RePontos();

    // Propaganda de site dentro de colchete/parênteses ("[ACESSE COMANDOTORRENTS.COM]",
    // "(baixe em www.site.com)") — só remove quando tem palavra-gatilho ou domínio dentro,
    // nunca um colchete às cegas (um filme pode legitimamente ter "[Extended]" no nome).
    [GeneratedRegex(@"[\[(][^\[\]()]*(?:acesse|baix(?:e|ar)|download|www\.|\.(?:com|net|org|tv|to|se|la|xyz|info)\b)[^\[\]()]*[\])]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReAnuncioSite();

    // Assinatura de grupo de release bem no fim do nome ("-RICKSZ", "-STARCKFILMES").
    [GeneratedRegex(@"-[A-Z0-9]{3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReAssinaturaRelease();

    // "WWW.SITE.COM" solto (sem colchete/parênteses ao redor) grudado no nome — mesma ideia
    // do ReAnuncioSite, mas pro caso mais comum de vir sem colchete nenhum.
    [GeneratedRegex(@"\bwww\.[a-z0-9-]+\.(?:com|net|org|to|se|la|xyz|info)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReDominioSolto();

    // Remove um marcador de sequência solto no FIM do título -- "Homem Aranha 2" -> "Homem
    // Aranha", "Toy Story 3" -> "Toy Story", "Rocky V" -> "Rocky". Começa em 2 de propósito:
    // o primeiro filme de uma franquia quase nunca tem "1" no nome. Romano só até X (10),
    // cobertura realista de sequência de filme -- sem $ maior que isso pra não arriscar
    // cortar palavra de verdade que termine parecido.
    [GeneratedRegex(@"\s+(?:(?:parte|part)\s+)?(?:[2-9]|1[0-9]|20|II|III|IV|VI|VII|VIII|IX|X|V)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSequenciaFranquia();

    // Pasta "coleção" (várias obras, cada uma no seu próprio subdiretório) — "Trilogia",
    // "Quadrilogia" etc. na pasta-mãe. Sem isso, cada filme (sozinho na sua subpasta) nunca
    // bate o limiar de "2+ arquivos" pra virar grupo e some espalhado como filme solto.
    [GeneratedRegex(@"\b(?:trilogia|tetralogia|quadrilogia|pentalogia|hexalogia|colecao|coleção|coletanea|coletânea|antologia|saga|box)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RePastaColecao();

    // Domínio do uploader NO COMEÇO do nome da pasta, tipo "COMANDO.TO - Contos do Loop
    // 1ª Temporada...": ao contrário do ReAnuncioSite/ReDominioSolto (ruído no meio/fim que só
    // precisa sumir), aqui o de-antes-do-" - " É o ruído e o nome de verdade vem DEPOIS —
    // cortar do jeito normal (que assume ruído depois de um prefixo limpo) devolvia "COMANDO
    // TO" como se fosse a série, perdendo "Contos do Loop" de vez.
    [GeneratedRegex(@"^\s*[a-z0-9-]+\.(?:com|net|org|to|se|la|xyz|info|tv)\s*-\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSiteNoComeco();

    // Número solto no início do nome, aceitando só espaço como separador ("01 O Paraíso
    // Verde.avi", sem hífen/ponto/dois-pontos nenhum) — mais permissivo que ReEpPrefixo de
    // propósito. Só é consultado quando ContarNumeradosPorPasta já garantiu que a pasta tem
    // MUITOS arquivos assim (ver MinArquivosAntologia); fora desse contexto controlado, um
    // filme comum cujo título começa com número ("12 Homens e uma Sentença") daria falso
    // positivo fácil demais.
    [GeneratedRegex(@"^\s*([0-9]{1,3})\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex ReEpNumeroAntologia();

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
        if (s.Length == 0) return "Sem pasta";

        // Se algum segmento ANTES do último (a pasta do próprio filme) é uma pasta-mãe de
        // coleção ("Quadrilogia A Era do Gelo .../A Era do Gelo 2 2006 .../filme.mkv"),
        // agrupa por ela — cada filme sozinho na sua subpasta nunca bateria "2+ arquivos".
        for (var i = 0; i < s.Length - 1; i++)
            if (RePastaColecao().IsMatch(s[i]))
                return string.Join('/', s[..(i + 1)]);

        return string.Join('/', s);
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

    // Abaixo disso, "pasta com N arquivos numerados sem temporada nenhuma" é tratada como
    // coleção de filmes (ex.: Coleção Rocky, 2 arquivos) — acima, como antologia sem
    // marcação de temporada (ex.: Além da Imaginação, 43 episódios). Bem acima de qualquer
    // coleção de filme realista, bem abaixo do que uma temporada de série costuma ter.
    private const int MinArquivosAntologia = 15;

    // ─── API pública ────────────────────────────────────────────────────

    public static bool EhExtra(string? arquivoPath)
    {
        var n = SemExtensao(NomeArquivo(arquivoPath));
        return ReExtra().IsMatch(n) || ReXbet().IsMatch(n) || ReNomeEhSoDominio().IsMatch(n);
    }

    /// <summary>Quantos arquivos, entre os informados, têm número solto no início do nome
    /// (mesmo padrão frouxo de <see cref="ReEpNumeroAntologia"/>) — agrupado por pasta.
    /// Usa-se pra decidir se uma pasta sem NENHUM marcador de temporada é uma coleção de
    /// filmes numerados (poucos arquivos, fica filme) ou uma antologia sem marcação nenhuma
    /// (muitos, vira episódios) — ver <see cref="MinArquivosAntologia"/>.</summary>
    public static Dictionary<string, int> ContarNumeradosPorPasta(IEnumerable<string?> arquivoPaths) =>
        arquivoPaths
            .Where(p => !string.IsNullOrEmpty(p) && ReEpNumeroAntologia().IsMatch(SemExtensao(NomeArquivo(p))))
            .GroupBy(PastaDe)
            .ToDictionary(g => g.Key, g => g.Count());

    public static bool EhEpisodio(string? arquivoPath, int arquivosNumeradosNaPasta = 0) =>
        OrdemEpisodio(arquivoPath, arquivosNumeradosNaPasta) is not null;

    /// <summary>(temporada, episódio) do arquivo, ou null se não é episódio. Ordem de sinal:
    /// SxxExx / NxNN no nome &gt; "Episódio N" no nome &gt; número solto no nome QUANDO a pasta
    /// diz a temporada (senão seria falso-positivo com pasta de filmes numerados) &gt; número
    /// solto (até sem separador) quando a pasta tem MUITOS arquivos assim, ver
    /// <paramref name="arquivosNumeradosNaPasta"/> e <see cref="ContarNumeradosPorPasta"/>.</summary>
    public static (int Temporada, int Episodio)? OrdemEpisodio(string? arquivoPath, int arquivosNumeradosNaPasta = 0)
    {
        var nome = SemExtensao(NomeArquivo(arquivoPath));

        var m = ReSxxExx().Match(nome);
        if (m.Success) return (ParseInt(m.Groups[1].Value), ParseInt(m.Groups[2].Value));

        m = ReTempEpi().Match(nome);
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

        if (tempPasta is null && arquivosNumeradosNaPasta >= MinArquivosAntologia)
        {
            m = ReEpNumeroAntologia().Match(nome);
            if (m.Success) return (0, ParseInt(m.Groups[1].Value));
        }
        return null;
    }

    /// <summary>Nome da série pra agrupar. Pasta normalizada (tira "N Temporada", "Parte N",
    /// ano, "[dominio]", " - grupo"), ou — quando o arquivo está solto — o prefixo do nome.</summary>
    public static string ChaveSerie(string? arquivoPath)
    {
        var segs = SegmentosPasta(arquivoPath);
        // Sobe da pasta mais interna pra mais externa. Cada nível: se É só um marcador de
        // temporada/arco (season/livro/parte...), pula pro pai sem nem tentar limpar aqui —
        // mesma ideia de antes. Senão, tenta limpar; se sobrar nome (não ficou vazio), é a
        // série. Se limpar tudo (o segmento era só "7ª Temporada - Completa - SITE.COM", tipo
        // ordinal+temporada ANTES de qualquer palavra, que ReCorteSerie também casa), sobe mais
        // um nível em vez de cair no fallback de "arquivo solto" — sem isso, "Hora da Aventura
        // - SITE/7ª Temporada - Completa - SITE/S07E05....mkv" nunca alcançava "Hora da
        // Aventura": o nível do meio limpava pra "" e o código antigo já tinha desistido ali.
        for (var idx = segs.Length - 1; idx >= 0; idx--)
        {
            var seg = segs[idx];
            if (idx > 0 && (ReSegmentoTemporadaPrefixo().IsMatch(seg) || ReSegmentoTemporadaExata().IsMatch(seg)))
                continue;

            var nome = ReSiteNoComeco().Replace(seg, "");
            nome = RePontos().Replace(nome, " ");
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
        // "Hora da Aventura" (grafia de release, preposição errada) vs "Hora de Aventura"
        // (título oficial BR de Adventure Time) — mesma série, palavra diferente.
        ["hora da aventura"] = "hora de aventura",
    };

    private static string NormalizarAcentoCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(SemAcentoMinuscula(c));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Chave de agrupamento tolerante a acento/maiúscula/espaço — mesma série
    /// escrita de jeitos ligeiramente diferentes em pastas de fontes diferentes ("Diários de
    /// Um Vampiro" / "Diarios de um vampiro" / "Diários de um Vampiro") cai na mesma chave.
    /// Também resolve apelidos conhecidos (<see cref="ApelidosSerie"/>). Só serve pra
    /// comparar/agrupar — quem exibe usa <see cref="ChaveSerie"/> sem alterar.</summary>
    public static string? ChaveAgrupamento(string? serie)
    {
        if (string.IsNullOrEmpty(serie)) return serie;
        var chave = NormalizarAcentoCase(serie);
        return ApelidosSerie.GetValueOrDefault(chave, chave);
    }

    /// <summary>Título sem o marcador de sequência do fim (ver <see cref="ReSequenciaFranquia"/>),
    /// preservando acento/maiúscula — pra exibir.</summary>
    public static string NomeBaseFranquia(string titulo) => ReSequenciaFranquia().Replace(titulo, "").TrimEnd();

    /// <summary>Chave de agrupamento de franquia por título — tolerante a acento/maiúscula
    /// (mesma normalização de <see cref="ChaveAgrupamento"/>), mais o corte do marcador de
    /// sequência (<see cref="NomeBaseFranquia"/>). Une filmes de uma franquia que moram em
    /// pastas TOTALMENTE separadas, sem pasta-mãe nenhuma ligando eles — diferente de
    /// <see cref="RePastaColecao"/>, que só resolve o caso de cada filme numa subpasta DENTRO
    /// de um wrapper "Trilogia .../". Só corta número/romano de sequência — não é
    /// correspondência de nome parecido (isso juntaria "Poder" com "Poder Absoluto" à toa);
    /// sem marcador de sequência no fim, o título passa direto e só bate com uma cópia
    /// idêntica dele mesmo.</summary>
    public static string? ChaveBaseFranquia(string? titulo) =>
        string.IsNullOrEmpty(titulo) ? titulo : NormalizarAcentoCase(NomeBaseFranquia(titulo));

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
        nome = ReDominioSolto().Replace(nome, " ");
        nome = ReRuido().Replace(nome, " ");
        if (pareceRelease) nome = ReAssinaturaRelease().Replace(nome, "");
        nome = Regex.Replace(nome, @"[\[\]()]+", " ");
        nome = Regex.Replace(nome, @"\s+", " ").Trim(' ', '-', '–', '—');

        return nome.Length > 0 ? nome : basename;
    }

    /// <summary>Classificação completa — o que o <c>FilmeResponse</c> entrega pras telas.
    /// <paramref name="contagemNumeradosPorPasta"/> vem de <see cref="ContarNumeradosPorPasta"/>
    /// calculado sobre o catálogo inteiro (ver <c>FilmeService.ListarAsync</c>) — sem isso,
    /// pasta de antologia sem marcador de temporada nunca vira episódio.</summary>
    public static ClassificacaoMidia Classificar(
        string? arquivoPath, string titulo, IReadOnlyDictionary<string, int>? contagemNumeradosPorPasta = null)
    {
        var pasta = PastaDe(arquivoPath);
        var arquivosNumerados = contagemNumeradosPorPasta?.GetValueOrDefault(pasta) ?? 0;
        var ordem = OrdemEpisodio(arquivoPath, arquivosNumerados);
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
