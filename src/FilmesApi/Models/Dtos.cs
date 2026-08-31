namespace FilmesApi.Models;

public record FilmeRequest(string Titulo, int? AnoLancamento, string? Diretor, string? ArquivoPath);
public record FilmeResponse(
    int Id, string Titulo, int? AnoLancamento, string? Diretor, string? ArquivoPath,
    bool Assistido, DateTime DataAdicionado,
    double? PosicaoSegundos, double? DuracaoSegundos);

public record ProgressoRequest(double Posicao, double? Duracao);
public record ProgressoResponse(int FilmeId, double PosicaoSegundos, double? DuracaoSegundos, DateTime AtualizadoEm);
public record ContinuarAssistindoResponse(
    int Id, string Titulo,
    double PosicaoSegundos, double? DuracaoSegundos, DateTime AtualizadoEm);
