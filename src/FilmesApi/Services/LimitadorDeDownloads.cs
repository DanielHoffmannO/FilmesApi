namespace FilmesApi.Services;

/// <summary>Teto de downloads simultâneos (<c>DownloadsSimultaneos</c>, 0 = sem limite). O disco de mídia é
/// um só e disputa com a TV; por isso o teto. A vaga é uma <see cref="IDisposable"/> idempotente: quem pega
/// precisa soltar ao terminar OU abortar.</summary>
public sealed class LimitadorDeDownloads(int limite)
{
    private readonly object _trava = new();
    private int _emAndamento;

    public int EmAndamento { get { lock (_trava) return _emAndamento; } }

    public static LimitadorDeDownloads Da(IConfiguration config) =>
        new(Math.Max(0, config.GetValue<int?>("DownloadsSimultaneos") ?? 2));

    /// <summary>A vaga, ou null se o limite já foi atingido.</summary>
    public IDisposable? TentarEntrar()
    {
        lock (_trava)
        {
            if (limite > 0 && _emAndamento >= limite) return null;
            _emAndamento++;
        }
        return new Vaga(this);
    }

    private sealed class Vaga(LimitadorDeDownloads dono) : IDisposable
    {
        private int _solta;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _solta, 1) == 1) return;
            lock (dono._trava) dono._emAndamento--;
        }
    }
}

/// <summary>FileStream que solta a vaga do limitador (e loga bytes/duração) quando a resposta acaba — o
/// ASP.NET descarta o stream tanto no fim normal quanto no aborto da conexão.</summary>
public sealed class StreamDeDownload : FileStream
{
    private readonly IDisposable _vaga;
    private readonly ILogger _log;
    private readonly int _id;
    private readonly CancellationToken _abortado;
    private readonly System.Diagnostics.Stopwatch _relogio = System.Diagnostics.Stopwatch.StartNew();
    private long _enviados;
    private int _encerrado;

    public StreamDeDownload(string caminho, IDisposable vaga, ILogger log, int id, CancellationToken abortado)
        : base(caminho, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true)
    {
        _vaga = vaga; _log = log; _id = id; _abortado = abortado;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await base.ReadAsync(buffer, ct);
        _enviados += n;
        return n;
    }

    public override int Read(byte[] array, int offset, int count)
    {
        var n = base.Read(array, offset, count);
        _enviados += n;
        return n;
    }

    private void Encerrar()
    {
        if (Interlocked.Exchange(ref _encerrado, 1) == 1) return;
        _vaga.Dispose();
        _log.LogInformation("Download do filme {Id}: {Bytes} bytes enviados em {Segundos:F1}s{Aborto}",
            _id, _enviados, _relogio.Elapsed.TotalSeconds, _abortado.IsCancellationRequested ? " (abortado)" : "");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Encerrar();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Encerrar();
    }
}
