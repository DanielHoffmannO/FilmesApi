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

    // ─── Metadados do TMDB (preenchidos em background quando TmdbApiKey está configurada) ───
    public int? TmdbId { get; set; }
    public string? TituloOriginal { get; set; }
    public string? PosterUrl { get; set; }
    public string? Sinopse { get; set; }
    /// <summary>Quando o enriquecimento rodou. Não-nulo mesmo quando não achou nada no TMDB
    /// (evita ficar tentando o mesmo arquivo toda hora).</summary>
    public DateTime? MetadadosEm { get; set; }

    /// <summary>Ponto de retomada da reprodução, se houver (ver <see cref="ProgressoReproducao"/>).</summary>
    public ProgressoReproducao? Progresso { get; set; }
}
