namespace FilmesApi.Models;

public class Filme
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public EGenero Genero { get; set; }
    public int? AnoLancamento { get; set; }
    public string? Diretor { get; set; }

    /// <summary>Caminho relativo do arquivo de vídeo na pasta de mídia.</summary>
    public string? ArquivoPath { get; set; }

    public bool Assistido { get; set; }
    public DateTime DataAdicionado { get; set; } = DateTime.UtcNow;
}

public enum EGenero : byte
{
    Acao = 1, Aventura, Comedia, Drama, Terror, Romance,
    FiccaoCientifica, Fantasia, Suspense, Crime, Animacao,
    Documentario, Musical, SuperHeroi, Familia
}
