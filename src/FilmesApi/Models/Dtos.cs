namespace FilmesApi.Models;

public record FilmeRequest(string Titulo, int? AnoLancamento, string? Diretor, string? ArquivoPath);
public record FilmeResponse(int Id, string Titulo, int? AnoLancamento, string? Diretor, string? ArquivoPath, bool Assistido, DateTime DataAdicionado);
