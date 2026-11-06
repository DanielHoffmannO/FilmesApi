namespace FilmesApi.Models;

/// <summary>Idioma de áudio padrão de uma série ("O Mentalista" sempre em <c>eng</c>). A chave é a mesma
/// <see cref="Services.MediaNomeParser.ChaveSerie"/> do agrupador, então vale pras pastas de todas as temporadas.</summary>
public class PreferenciaSerie
{
    public string ChaveSerie { get; set; } = "";

    /// <summary>Código ffprobe normalizado (<c>eng</c>, <c>por</c>…), ver <see cref="Services.EscolhaAudio.NormalizarIdioma"/>.</summary>
    public string IdiomaAudio { get; set; } = "";
    // IdiomaAudio vazio = série só com legenda padrão (sem preferência de áudio).

    /// <summary>Legenda padrão da série: <c>"off"</c> (sem legenda) ou o idioma (<c>eng</c>, <c>por</c>…). Null = como sempre
    /// (a TV liga sozinha quando o áudio não é em português).</summary>
    public string? IdiomaLegenda { get; set; }
}

/// <summary>O que o controle liga por temporada: pular a abertura sozinho e ir ao próximo episódio sozinho. Tudo desligado por padrão.</summary>
public class PreferenciaTemporada
{
    public string ChaveSerie { get; set; } = "";
    public int Temporada { get; set; }
    public bool PularAbertura { get; set; }
    public bool ProximoAutomatico { get; set; }
}

/// <summary>Faixa de áudio escolhida à mão pra um episódio (índice da faixa no arquivo). Antes vivia só em memória.</summary>
public class PreferenciaEpisodio
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }
    public int AudioIndex { get; set; }
}
