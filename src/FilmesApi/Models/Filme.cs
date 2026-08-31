namespace FilmesApi.Models;

public class Filme
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public int? AnoLancamento { get; set; }
    public string? Diretor { get; set; }

    /// <summary>Caminho relativo do arquivo de vídeo na pasta de mídia.</summary>
    public string? ArquivoPath { get; set; }

    public bool Assistido { get; set; }
    public DateTime DataAdicionado { get; set; } = DateTime.UtcNow;

    /// <summary>Ponto de retomada da reprodução, se houver (ver <see cref="ProgressoReproducao"/>).</summary>
    public ProgressoReproducao? Progresso { get; set; }
}
