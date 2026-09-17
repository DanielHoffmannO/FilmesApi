namespace FilmesApi.Models;

/// <summary>Um filme na lista. Os campos de classificação (<c>Serie</c>, <c>EhEpisodio</c>,
/// <c>Rotulo</c>…) são computados no servidor por <see cref="Services.MediaNomeParser"/> —
/// as telas só renderizam, não têm mais regex.</summary>
public record FilmeResponse(
    int Id, string Titulo, int? AnoLancamento, string? ArquivoPath,
    bool Assistido, DateTime DataAdicionado,
    double? PosicaoSegundos, double? DuracaoSegundos,
    string? PosterUrl = null, string? Sinopse = null, string? TituloOriginal = null,
    bool EhEpisodio = false, bool EhExtra = false, string? Serie = null, string? SerieChave = null,
    int? Temporada = null, int? Episodio = null, string Rotulo = "", string Pasta = "Sem pasta");

public record ScanResultado(int Importados, int Removidos, int TitulosLimpos = 0);

/// <summary>Pasta com 2+ arquivos sem episódio (filme + extras: trailer, sample…).</summary>
public record PastaAgrupada(string Pasta, List<FilmeResponse> Itens);

/// <summary>Uma série com seus episódios já ordenados (temporada, episódio, título).
/// <c>Chave</c> é <see cref="Services.MediaNomeParser.ChaveAgrupamento"/> — estável entre
/// renders, serve de id de UI (expandir/colapsar). <c>Nome</c> é pra exibir.</summary>
public record SerieAgrupada(string Chave, string Nome, List<FilmeResponse> Episodios);

/// <summary>
/// O catálogo já filtrado (tipo/visto/busca) e agrupado (filme solto / pasta de filme+extras /
/// série com episódios), pronto pra tela desenhar sem nenhuma lógica própria. Calculado uma
/// vez no servidor (<see cref="Services.FilmeService.MontarTela"/>) em vez de replicado em
/// cada uma das 3 telas (index.html, controle.html, tv.html) — era assim que o mesmo bug de
/// agrupamento (acento/maiúscula, arco "Livro N", release com SxxExx na pasta) precisava ser
/// corrigido 3 vezes.
/// </summary>
/// <param name="ContinuarAssistindo">Filmes com retomada pendente — só populado quando tipo e
/// visto estão neutros ("all"), pra não misturar com um filtro ativo.</param>
/// <param name="FilmesSoltos">Filmes sem pasta compartilhada (ou pasta com só 1 arquivo real).</param>
/// <param name="PastasFilme">Pastas com 2+ arquivos reais sem episódio (filme + extras).</param>
/// <param name="Series">Séries com episódios agrupados por <see cref="Services.MediaNomeParser.ChaveAgrupamento"/>.</param>
/// <param name="CatalogoVazio">Catálogo inteiro (sem filtro nenhum) não tem nenhum filme —
/// diferente de "nada bate o filtro atual". Vem de graça (o servidor já classificou tudo pra
/// montar esta mesma resposta), poupa as 3 telas de um <c>GET /api/filmes</c> à parte só pra
/// essa pergunta.</param>
public record TelaCatalogoResponse(
    List<FilmeResponse> ContinuarAssistindo,
    List<FilmeResponse> FilmesSoltos,
    List<PastaAgrupada> PastasFilme,
    List<SerieAgrupada> Series,
    bool CatalogoVazio);

/// <summary>Uma faixa de legenda embutida. <c>Idx</c> é o índice relativo (0,1,2… na ordem
/// do ffprobe) usado no endpoint <c>/legenda/{idx}</c>. <c>Convertivel</c> = é texto e dá
/// pra servir como WebVTT (bitmap tipo PGS não dá).</summary>
public record LegendaInfo(
    int Idx, string Codec, string? Idioma, string? Titulo, bool Forced, bool Default, bool Convertivel);

public record ProgressoRequest(double Posicao, double? Duracao);
public record ProgressoResponse(int FilmeId, double PosicaoSegundos, double? DuracaoSegundos, DateTime AtualizadoEm);
public record ContinuarAssistindoResponse(
    int Id, string Titulo,
    double PosicaoSegundos, double? DuracaoSegundos, DateTime AtualizadoEm);
