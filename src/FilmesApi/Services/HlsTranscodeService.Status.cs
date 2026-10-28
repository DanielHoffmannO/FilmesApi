namespace FilmesApi.Services;

public partial class HlsTranscodeService
{
    private readonly object _cacheStatsLock = new();

    /// <summary>Retrato do estado do transcode pra página de status (que faz poll a cada ~3 s).
    /// A varredura de tamanho do cache em disco é feita no máximo a cada <see cref="CachesDeDisco.ValidadeDoRetrato"/>.</summary>
    public Models.HlsStatusSnapshot ObterSnapshot()
    {
        var jobs = _jobs.Keys.OrderBy(x => x).ToArray();
        var encodando = Math.Max(0, _maxJobs - _slotEncoder.CurrentCount);

        (long HlsBytes, int HlsItens, long RemuxBytes, int RemuxItens, DateTime _) stats;
        lock (_cacheStatsLock)
        {
            if (DateTime.UtcNow - _cacheStats.Quando > CachesDeDisco.ValidadeDoRetrato)
            {
                var (hlsBytes, hlsItens) = MedirCacheHls();
                var (remuxBytes, remuxItens) = MedirCacheRemux();
                _cacheStats = (hlsBytes, hlsItens, remuxBytes, remuxItens, DateTime.UtcNow);
            }
            stats = _cacheStats;
        }

        return new Models.HlsStatusSnapshot(
            Encodando: encodando,
            NaFila: Math.Max(0, jobs.Length - encodando),
            Completos: _completos.Count,
            Falhas: _falhas.Count,
            FilmesEmJob: jobs,
            FilmesComFalha: _falhas.Keys.OrderBy(x => x).ToArray(),
            CacheBytes: stats.HlsBytes,
            CacheMaxBytes: _cacheMaxBytes == long.MaxValue ? 0 : _cacheMaxBytes,
            CacheItens: stats.HlsItens,
            RemuxBytes: stats.RemuxBytes,
            RemuxMaxBytes: _remuxCacheMaxBytes == long.MaxValue ? 0 : _remuxCacheMaxBytes,
            RemuxItens: stats.RemuxItens,
            EscritoBytes: _escrita.Bytes,
            EscritoDesde: _escrita.Desde);
    }

    private (long Bytes, int Itens) MedirCacheHls()
    {
        long total = 0;
        var itens = 0;
        try
        {
            if (Directory.Exists(_cachePath))
                foreach (var dir in Directory.EnumerateDirectories(_cachePath))
                {
                    itens++;
                    total += EscritaDeCache.TamanhoDaPasta(dir);
                }
        }
        catch (IOException) { }
        return (total, itens);
    }

    private (long Bytes, int Itens) MedirCacheRemux()
    {
        try
        {
            var itens = Directory.Exists(_remuxCachePath) ? Directory.EnumerateFiles(_remuxCachePath, NomesDeCache.PadraoRemux).Count() : 0;
            return (EscritaDeCache.TamanhoDaPasta(_remuxCachePath), itens);
        }
        catch (IOException) { return (0, 0); }
    }
}
