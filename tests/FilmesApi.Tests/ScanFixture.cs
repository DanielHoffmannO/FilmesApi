using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FilmesApi.Tests;

/// <summary>FilmeService de verdade (SQLite em memória + pasta de mídia temporária), montado pelo
/// mesmo contêiner de DI que o Program usa — pra testar o scan ponta a ponta.</summary>
internal sealed class ScanFixture : IDisposable
{
    private readonly SqliteConnection _conexao = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    public string Raiz { get; } = TestesComuns.CriarPastaTemp();
    public string Media => Path.Combine(Raiz, "media");
    public IServiceScopeFactory Escopos => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>Linhas de log (nível + mensagem) de tudo que os serviços escreveram.</summary>
    public List<string> Logs { get; } = [];

    public ScanFixture(double estabilidadeSegundos = 0, double? intervaloMinutos = null,
        string? ffmpeg = null, string? ffprobe = null, Dictionary<string, string?>? configExtra = null)
    {
        Directory.CreateDirectory(Media);
        _conexao.Open();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaPath"] = Media,
            ["HlsCachePath"] = Path.Combine(Raiz, "hls"),
            ["RemuxCachePath"] = Path.Combine(Raiz, "remux"),
            ["SubtitleCachePath"] = Path.Combine(Raiz, "subs"),
            ["ScanEstabilidadeSegundos"] = estabilidadeSegundos.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ScanIntervaloMinutos"] = intervaloMinutos?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }.Concat(configExtra ?? []).ToDictionary(kv => kv.Key, kv => kv.Value)).Build();

        _provider = MontarServicos(config, ffmpeg, ffprobe);

        using var escopo = _provider.CreateScope();
        escopo.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public EstadoMontagens Montagens => _provider.GetRequiredService<EstadoMontagens>();

    public string CacheHls => Path.Combine(Raiz, "hls");
    public string CacheRemux => Path.Combine(Raiz, "remux");

    /// <summary>Roda <paramref name="acao"/> com os serviços de um escopo novo (FilmeService, ProgressoService…).</summary>
    public async Task<T> NoEscopoAsync<T>(Func<IServiceProvider, Task<T>> acao)
    {
        using var escopo = _provider.CreateScope();
        return await acao(escopo.ServiceProvider);
    }

    /// <summary>Id no banco do arquivo (relativo à pasta de mídia).</summary>
    public async Task<int> IdDeAsync(string relativo) => await NoEscopoAsync(async sp =>
        await sp.GetRequiredService<AppDbContext>().Filmes.Where(f => f.ArquivoPath == relativo).Select(f => f.Id).SingleAsync());

    /// <summary>Simula um episódio já transcodificado: segmentos HLS e o .mp4 do remux em cache.</summary>
    public (string DirHls, string Remux) CriarCache(int filmeId)
    {
        var dir = Path.Combine(CacheHls, filmeId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "playlist.m3u8"), "#EXTM3U");
        File.WriteAllText(Path.Combine(dir, "seg_00000.ts"), "x");
        Directory.CreateDirectory(CacheRemux);
        var remux = Path.Combine(CacheRemux, $"{filmeId}.mp4");
        File.WriteAllText(remux, "mp4");
        return (dir, remux);
    }

    private ServiceProvider MontarServicos(IConfiguration config, string? ffmpeg, string? ffprobe)
    {
        var servicos = new ServiceCollection();
        servicos.AddSingleton<IConfiguration>(config);
        servicos.AddLogging(b => b.AddProvider(new LogsEmMemoria(Logs)));
        servicos.AddSingleton<IHostApplicationLifetime, LifetimeFalso>();
        servicos.AddDbContext<AppDbContext>(o => o.UseSqlite(_conexao));
        servicos.AddSingleton(new FfmpegOptions(ffmpeg ?? "ffmpeg-nao-usado", ffprobe ?? "ffprobe-nao-usado"));
        servicos.AddSingleton<MediaProbeService>();
        servicos.AddSingleton<ClassificacaoCache>();
        servicos.AddSingleton<EstadoMontagens>();
        servicos.AddSingleton<RkmppCapabilityService>();
        servicos.AddSingleton<ThermalService>();
        servicos.AddSingleton<HlsTranscodeService>();
        servicos.AddSingleton<SubtitleService>();
        servicos.AddScoped<FilmeService>();
        servicos.AddScoped<ProgressoService>();
        return servicos.BuildServiceProvider();
    }

    /// <summary>Cria (com conteúdo) o arquivo relativo à pasta de mídia.</summary>
    public string Arquivo(string relativo, string conteudo = "video")
    {
        var caminho = Path.Combine(Media, relativo);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(caminho, conteudo);
        return caminho;
    }

    public async Task<ScanResultado> ScanAsync()
    {
        using var escopo = _provider.CreateScope();
        return await escopo.ServiceProvider.GetRequiredService<FilmeService>().ScanMediaAsync();
    }

    public async Task<List<string>> CaminhosNoBancoAsync()
    {
        using var escopo = _provider.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Filmes.AsNoTracking().Select(f => f.ArquivoPath!).OrderBy(c => c).ToListAsync();
    }

    public async Task MarcarAssistidoComProgressoAsync(string relativo)
    {
        using var escopo = _provider.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var filme = await db.Filmes.SingleAsync(f => f.ArquivoPath == relativo);
        filme.Assistido = true;
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conexao.Dispose();
        try { Directory.Delete(Raiz, recursive: true); } catch (IOException) { }
    }

    private sealed class LogsEmMemoria(List<string> destino) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoria) => new Registro(destino);
        public void Dispose() { }

        private sealed class Registro(List<string> destino) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? ex, Func<TState, Exception?, string> formatar)
            {
                lock (destino) destino.Add($"{nivel}: {formatar(estado, ex)}");
            }
        }
    }

    private sealed class LifetimeFalso : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
