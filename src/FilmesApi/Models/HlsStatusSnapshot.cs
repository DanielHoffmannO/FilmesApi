namespace FilmesApi.Models;

/// <summary>Estado do subsistema de transcode HLS (ver <c>status.html</c>).</summary>
public record HlsStatusSnapshot(
    int JobsAtivos,
    int Encodando,
    int NaFila,
    int Completos,
    int Falhas,
    int[] FilmesEmJob,
    int[] FilmesComFalha,
    long CacheBytes,
    long CacheMaxBytes,
    int CacheItens);
