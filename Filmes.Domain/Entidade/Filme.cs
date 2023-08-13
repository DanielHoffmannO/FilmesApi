using Filmes.Domain.Enum;
using Microsoft.EntityFrameworkCore;

namespace Filmes.Domain.Entidade;

public class Filme : Entity<Filme, short>
{
    public Filme() { }

    public Filme(string Titulo, EGenero Genero, decimal Duracao, bool Assistido)
    {
        this.Titulo = Titulo;
        this.Genero = Genero;
        this.Duracao = Duracao;
        this.Assistido = Assistido;
    }

    public string Titulo { get; set; }
    public EGenero Genero { get; set; }
    public decimal Duracao { get; set; }
    public bool Assistido { get; set; }
}
