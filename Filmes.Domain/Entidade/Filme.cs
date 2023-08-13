using Filmes.Domain.Enum;
using Microsoft.EntityFrameworkCore;

namespace Filmes.Domain.Entidade;

public class Filme : Entity<Filme, short>
{
    public Filme() { }
    public Filme(string titulo, EGenero genero, decimal duracaoMinutos, int anoLancamento, string diretor)
    {
        Titulo = titulo;
        Genero = genero;
        DuracaoMinutos = duracaoMinutos;
        AnoLancamento = anoLancamento;
        Diretor = diretor;
        DataCadastro = DateTime.Now;
        DataAssistido = DateTime.UnixEpoch;
    }

    public string Titulo { get; private set; }
    public EGenero Genero { get; private set; }
    public decimal DuracaoMinutos { get; private set; }
    public int AnoLancamento { get; private set; }
    public string Diretor { get; private set; }
    public DateTime DataCadastro { get; private set; } = DateTime.Now;
    public DateTime DataAssistido { get; private set; } = DateTime.UnixEpoch;

    public void MarcarComoAssistido() => DataAssistido = DateTime.Now;
    public void Atualizar(string titulo, byte? genero, decimal? duracaoMinutos, int? anoLancamento, string diretor)
    {
        Titulo = String.IsNullOrEmpty(titulo) ? Titulo : titulo;
        Diretor = String.IsNullOrEmpty(diretor) ? Diretor : diretor;
        Genero = genero.HasValue ? (EGenero)genero : Genero;
        DuracaoMinutos = duracaoMinutos ?? DuracaoMinutos;
        AnoLancamento = anoLancamento ?? AnoLancamento;
    }
}
