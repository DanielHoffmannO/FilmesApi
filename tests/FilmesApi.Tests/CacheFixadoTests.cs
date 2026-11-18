using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>"Manter no cache": o fixado nunca é expulso (teto, limpeza automática, "limpar cache") e a lista de fixados persiste.</summary>
public class CacheFixadoTests : IDisposable
{
    private static readonly DateTime Agora = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _raiz = TestesComuns.CriarPastaTemp();
    private readonly string _hls, _remux;
    private readonly SqliteConnection _conexao = new("Data Source=:memory:");
    private readonly ServiceProvider _sp;

    public CacheFixadoTests()
    {
        _hls = Path.Combine(_raiz, "hls"); _remux = Path.Combine(_raiz, "remux");
        Directory.CreateDirectory(_hls); Directory.CreateDirectory(_remux);
        _conexao.Open();
        _sp = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseSqlite(_conexao)).BuildServiceProvider();
        using var escopo = _sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        db.Filmes.AddRange(new Filme { Id = 1, Titulo = "A" }, new Filme { Id = 2, Titulo = "B" });
        db.SaveChanges();
    }

    public void Dispose() { _sp.Dispose(); _conexao.Dispose(); Directory.Delete(_raiz, recursive: true); }

    private string Mp4(int id, TimeSpan parado, int bytes = 1000)
    {
        var f = Path.Combine(_remux, id + ".mp4");
        File.WriteAllBytes(f, new byte[bytes]);
        File.SetLastWriteTimeUtc(f, Agora - parado);
        return f;
    }

    private HlsTranscodeService NovoTranscode()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["HlsCachePath"] = _hls, ["RemuxCachePath"] = _remux }).Build();
        var ff = new FfmpegOptions("ffmpeg-nao-usado", "ffprobe-nao-usado");
        return new HlsTranscodeService(ff, config, new RkmppCapabilityService(ff, config, NullLogger<RkmppCapabilityService>.Instance),
            new ThermalService(config, NullLogger<ThermalService>.Instance), new MediaProbeService(ff, NullLogger<MediaProbeService>.Instance),
            NullLogger<HlsTranscodeService>.Instance, new Vida(),
            new PreferenciasAudioService(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()));
    }

    private sealed class Vida : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public void Despejo_por_teto_nunca_toca_no_fixado_mesmo_assistido_e_velho()
    {
        var velho = TimeSpan.FromDays(30);
        var (a, b, c) = (Mp4(1, velho), Mp4(2, velho), Mp4(3, velho));

        var despejados = HlsTranscodeService.DespejarExcedenteRemux(_remux, 1500, DateTime.UtcNow.AddYears(1),
            assistido: id => id <= 2, fixado: id => id == 1);

        Assert.True(File.Exists(a));            // fixado: fica, mesmo com o teto estourado
        Assert.False(File.Exists(b));
        Assert.DoesNotContain("1.mp4", despejados.Select(d => d.Nome));
    }

    [Fact]
    public void Limpeza_automatica_poupa_o_fixado_assistido_parado()
    {
        Mp4(1, TimeSpan.FromDays(30)); Mp4(2, TimeSpan.FromDays(30));
        var op = new OpcoesLimpeza(TimeSpan.FromMinutes(120), TimeSpan.FromDays(14));
        var ganchos = new GanchosDeCache(_ => null, (_, _) => false, (id, _) => File.Delete(Path.Combine(_remux, id + ".mp4")));

        LimpezaDeCache.Varrer(_hls, _remux, new Dictionary<int, bool> { [1] = true, [2] = true }, Agora, op, ganchos, new HashSet<int> { 1 });

        Assert.True(File.Exists(Path.Combine(_remux, "1.mp4")));
        Assert.False(File.Exists(Path.Combine(_remux, "2.mp4")));
    }

    [Fact]
    public void Limpar_cache_apaga_so_o_que_nao_esta_fixado()
    {
        // mtime bem antigo: fora da margem de uso de 5 min
        Mp4(1, TimeSpan.FromDays(300)); Mp4(2, TimeSpan.FromDays(300));
        var r = NovoTranscode().LimparNaoFixados(new HashSet<int> { 1 });

        Assert.Equal(1, r.Itens);
        Assert.True(File.Exists(Path.Combine(_remux, "1.mp4")));
        Assert.False(File.Exists(Path.Combine(_remux, "2.mp4")));
    }

    [Fact]
    public async Task Fixar_e_desafixar_persistem_ignoram_inexistentes_e_nao_duplicam()
    {
        var svc = new CacheFixadoService(_sp.GetRequiredService<IServiceScopeFactory>(), NovoTranscode(), new ConfigurationBuilder().Build(),
            NullLogger<CacheFixadoService>.Instance);

        Assert.Equal(2, await svc.FixarAsync([1, 2, 2, 999]));   // 999 não existe; 2 repetido conta uma vez
        Assert.Equal(0, await svc.FixarAsync([1]));              // já fixado
        Assert.Equal([1, 2], svc.Ordem);

        var outro = new CacheFixadoService(_sp.GetRequiredService<IServiceScopeFactory>(), NovoTranscode(), new ConfigurationBuilder().Build(),
            NullLogger<CacheFixadoService>.Instance);
        await outro.CarregarAsync();                              // "reiniciou": a lista volta do banco
        Assert.Equal([1, 2], outro.Ordem);

        Assert.Equal(1, await svc.DesafixarAsync([1, 777]));
        Assert.Equal([2], svc.Ordem);
        Assert.Contains(2, svc.Fixados);
    }

    [Fact]
    public async Task Apagar_o_filme_leva_o_fixado_junto()
    {
        var svc = new CacheFixadoService(_sp.GetRequiredService<IServiceScopeFactory>(), NovoTranscode(), new ConfigurationBuilder().Build(),
            NullLogger<CacheFixadoService>.Instance);
        await svc.FixarAsync([1]);
        using (var escopo = _sp.CreateScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
            db.Filmes.Remove(db.Filmes.Single(f => f.Id == 1));
            db.SaveChanges();
        }
        await svc.CarregarAsync();
        Assert.Empty(svc.Ordem);
    }

    [Fact]
    public void Politica_do_teto_dos_fixados_e_do_limite_por_rodada()
    {
        const long gb = 1L << 30;
        Assert.True(PoliticaCacheFixado.CabeNoTetoFixado(39 * gb, 1 * gb, 40 * gb));
        Assert.False(PoliticaCacheFixado.CabeNoTetoFixado(39 * gb, 2 * gb, 40 * gb));
        Assert.True(PoliticaCacheFixado.CabeNoTetoFixado(1000 * gb, gb, long.MaxValue));
        Assert.False(PoliticaCacheFixado.AtingiuLimiteDaRodada(4 * gb, 5 * gb));
        Assert.True(PoliticaCacheFixado.AtingiuLimiteDaRodada(5 * gb, 5 * gb));
        Assert.False(PoliticaCacheFixado.AtingiuLimiteDaRodada(100 * gb, 0));   // 0 = sem limite
    }
}
