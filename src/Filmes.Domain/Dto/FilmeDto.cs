using Filmes.Domain.Enum;

namespace Filmes.Domain.Dto;

public record FilmeDto(string Titulo, byte? Genero, decimal? DuracaoMinutos, int? AnoLancamento, string? Diretor);


