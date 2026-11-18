namespace FilmesApi.Models;

/// <summary>"Manter no cache": o título (filme ou episódio) que o dono marcou pra ter sempre uma cópia pronta pra tocar no cache
/// do NVMe (<c>remux/{id}.mp4</c>). Fixado nunca é expulso pelo teto nem pela limpeza; o <see cref="Services.CacheFixadoService"/> prepara a cópia aos poucos.</summary>
public class CacheFixado
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }
    public DateTime FixadoEm { get; set; } = DateTime.UtcNow;
}
