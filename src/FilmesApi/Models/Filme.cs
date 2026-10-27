namespace FilmesApi.Models;

public class Filme
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;

    public int? AnoLancamento { get; set; }

    /// <summary>Caminho relativo do arquivo de vídeo na pasta de mídia.</summary>
    public string? ArquivoPath { get; set; }

    public bool Assistido { get; set; }

    /// <summary>Quando virou assistido (alimenta "o próximo episódio" da fila do controle). Null em quem já
    /// estava assistido antes da coluna existir e em quem foi desmarcado.</summary>
    public DateTime? AssistidoEm { get; set; }

    /// <summary>Marca/desmarca já carimbando <see cref="AssistidoEm"/> — use no lugar de mexer nos dois campos (o <c>ExecuteUpdate</c> do
    /// <c>ConcluirAsync</c> não passa por aqui e seta os dois por conta própria).</summary>
    public void DefinirAssistido(bool assistido)
    {
        Assistido = assistido;
        AssistidoEm = assistido ? DateTime.UtcNow : null;
    }
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
