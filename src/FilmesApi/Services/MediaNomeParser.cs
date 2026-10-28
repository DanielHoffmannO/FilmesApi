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
/// <param name="TituloEpisodio">Título do episódio que o nome do arquivo traz depois do marcador
/// ("O Apagador" em <c>... - S01E05 - O Apagador.mp4</c>), sem ruído de release nem site; null quando não tem.</param>
public record ClassificacaoMidia(
    bool EhEpisodio, bool EhExtra, string? Serie, string? SerieChave,
    int? Temporada, int? Episodio, string Rotulo, string Pasta, string? TituloEpisodio = null);

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

    // O mesmo "WWW.SITE.COM", mas DEPOIS de RePontos já ter trocado os pontos por espaço
    // ("WWW BLUDV COM") -- no nome da pasta o domínio só chega assim.
    [GeneratedRegex(@"\bwww\s+[a-z0-9-]+\s+(?:com|net|org|tv|to|se|la|xyz|info)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReDominioSemPontos();

    // "Trilogia Matrix", "Quadrilogia A Era do Gelo": a palavra da coleção só descreve o pacote,
    // o nome da obra vem depois. Só no começo, e só as palavras sem risco -- "Saga", "Box" e
    // "Antologia" podem ser parte do título de verdade ("Saga Crepúsculo"), então ficam.
    [GeneratedRegex(@"^(?:trilogia|tetralogia|quadrilogia|pentalogia|hexalogia|colecao|coleção|coletanea|coletânea)\s+(?:d[aeo]s?\s+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RePrefixoColecao();

    // "1999 - 2003": intervalo de anos do pacote. Um ano só ("Filme 2020") não é tocado.
    [GeneratedRegex(@"\b(?:19|20)[0-9]{2}\s*[-–—]\s*(?:19|20)[0-9]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReIntervaloAnos();

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

    // Acrônimo + temporada + episódio tudo GRUDADO, sem separador nenhum ("DDUVT08EP13" =
    // "Diários De Um Vampiro" T08 EP13, nome que o site encurtou pras iniciais). Sem \b antes
    // do T de propósito: o T vem colado direto no acrônimo, sem fronteira \w/\W ali pro \b
    // enxergar. O acrônimo em si não entra na regex — só marca onde ChaveSerie corta o prefixo
    // (ver uso em ChaveSerie) — e "dduv" tem apelido pra a chave canônica em ApelidosSerie.
    [GeneratedRegex(@"T([0-9]{1,2})EP([0-9]{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReTEpColado();

    // Número solto no início do nome, aceitando espaço OU hífen colado como separador ("01 O
    // Paraíso Verde.avi", mas também "01- Pingu ajuda com a incubação.avi" — hífen GRUDADO no
    // número, sem espaço antes, caso real da série Pingu) — mais permissivo que ReEpPrefixo de
    // propósito. Só é consultado quando ContarNumeradosPorPasta já garantiu que a pasta tem
    // MUITOS arquivos assim (ver MinArquivosAntologia); fora desse contexto controlado, um
    // filme comum cujo título começa com número ("12 Homens e uma Sentença") daria falso
    // positivo fácil demais.
    [GeneratedRegex(@"^\s*([0-9]{1,3})(?:\s*-\s*|\s+)\S", RegexOptions.CultureInvariant)]
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

        m = ReTEpColado().Match(nome);
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
        if (i < 0) i = IndiceOuMenos1(ReTEpColado(), n);
        var prefixo = i > 0 ? n[..i] : n;
        prefixo = RePontos().Replace(prefixo, " ").TrimEnd(' ', '-').Trim();
        return prefixo.Length > 0 ? prefixo : n;
    }
}
