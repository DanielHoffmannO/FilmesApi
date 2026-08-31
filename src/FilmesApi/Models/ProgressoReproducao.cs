namespace FilmesApi.Models;

/// <summary>
/// Onde a reprodução de um filme parou, para retomar depois ("continuar assistindo").
/// Uma linha por filme (relação 1:1 com <see cref="Filme"/>).
/// </summary>
public class ProgressoReproducao
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }

    /// <summary>Segundo em que a reprodução parou.</summary>
    public double PosicaoSegundos { get; set; }

    /// <summary>Duração total do vídeo, quando o player conseguiu informar (pode faltar durante transcode HLS).</summary>
    public double? DuracaoSegundos { get; set; }

    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}
