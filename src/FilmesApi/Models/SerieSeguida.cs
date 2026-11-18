namespace FilmesApi.Models;

/// <summary>"Seguir no cache": a série (ou novela) cuja janela dos próximos episódios não assistidos o servidor mantém pronta no cache do NVMe
/// (ver <see cref="Services.PoliticaSeguirSerie"/> e <see cref="Services.SeguirSerieService"/>). <c>ChaveSerie</c> é a <c>SerieChave</c> do catálogo.</summary>
public class SerieSeguida
{
    public string ChaveSerie { get; set; } = "";
    public DateTime Desde { get; set; } = DateTime.UtcNow;

    /// <summary>Tamanho da janela (quantos episódios não assistidos à frente ficam prontos).</summary>
    public int Janela { get; set; } = 5;
}
