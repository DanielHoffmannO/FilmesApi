namespace FilmesApi.Models;

/// <summary>Idioma de áudio padrão de uma série ("O Mentalista" sempre em <c>eng</c>). A chave é a mesma
/// <see cref="Services.MediaNomeParser.ChaveSerie"/> do agrupador, então vale pras pastas de todas as temporadas.</summary>
public class PreferenciaSerie
{
    public string ChaveSerie { get; set; } = "";

    /// <summary>Código ffprobe normalizado (<c>eng</c>, <c>por</c>…), ver <see cref="Services.EscolhaAudio.NormalizarIdioma"/>.</summary>
    public string IdiomaAudio { get; set; } = "";
}

/// <summary>Faixa de áudio escolhida à mão pra um episódio (índice da faixa no arquivo). Antes vivia só em memória.</summary>
public class PreferenciaEpisodio
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }
    public int AudioIndex { get; set; }
}
