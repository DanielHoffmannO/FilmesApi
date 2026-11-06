namespace FilmesApi.Models;

/// <summary>Onde fica a abertura de um episódio (achada por <see cref="Services.AnaliseDeAberturaService"/>). Uma linha por
/// episódio já analisado; <see cref="InicioSegundos"/>/<see cref="FimSegundos"/> nulos = analisou e não achou nada.</summary>
public class AberturaEpisodio
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }
    public double? InicioSegundos { get; set; }
    public double? FimSegundos { get; set; }
    public DateTime AnalisadoEm { get; set; } = DateTime.UtcNow;
}
