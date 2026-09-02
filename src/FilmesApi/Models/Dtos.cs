namespace FilmesApi.Models;

public record FilmeRequest(string Titulo, int? AnoLancamento, string? Diretor, string? ArquivoPath);
public record FilmeResponse(
    int Id, string Titulo, int? AnoLancamento, string? Diretor, string? ArquivoPath,
    bool Assistido, DateTime DataAdicionado,
    double? PosicaoSegundos, double? DuracaoSegundos,
    string? PosterUrl = null, string? Sinopse = null, string? TituloOriginal = null);

public record ScanResultado(int Importados, int Removidos);

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
