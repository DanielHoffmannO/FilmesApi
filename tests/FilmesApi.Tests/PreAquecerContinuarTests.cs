using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Pré-aquecer o remux do "continuar assistindo" número 1 (a TV sem HLS espera ~25 s num episódio e ~4 min num
/// filme de 4,8 GB), mas só quando não atrapalha nada. Usa um ffprobe/ffmpeg FALSOS (scripts): o ffprobe diz que o
/// arquivo tem vídeo H.264 + áudio AC3 (precisa de remux) e o ffmpeg só grava o arquivo de saída.</summary>
public class PreAquecerContinuarTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    private string FfprobeComAudio(string codec) => TestesComuns.ScriptFalso(_bin, $"ffprobe-{codec}.sh", $$$"""
        echo '{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720},{"index":1,"codec_type":"audio","codec_name":"{{{codec}}}","channels":6,"tags":{"language":"eng"}}],"format":{"duration":"3600.0"}}'
        """);

    /// <summary>ffmpeg falso: demora <paramref name="segundos"/> e grava 2 MB no último argumento (o arquivo de saída).</summary>
    private string FfmpegFalso(double segundos) => TestesComuns.ScriptFalso(_bin, $"ffmpeg-{segundos}.sh", $"""
        for a; do ultimo="$a"; done
        sleep {segundos.ToString(System.Globalization.CultureInfo.InvariantCulture)}
        head -c 2097152 /dev/zero > "$ultimo"
        """);

    private async Task<(ScanFixture F, int Id, PreAquecerContinuarService Servico)> Cenario(
        string audio = "ac3", double ffmpegSegundos = 0.2, Dictionary<string, string?>? config = null, bool comProgresso = true)
    {
        var f = new ScanFixture(ffmpeg: FfmpegFalso(ffmpegSegundos), ffprobe: FfprobeComAudio(audio), configExtra: config);
        _fixtures.Add(f);
        f.Arquivo("Filmes/Matrix.1999.mkv", new string('x', 4096));
        await f.ScanAsync();
        var id = await f.IdDeAsync("Filmes/Matrix.1999.mkv");
        if (comProgresso)
            await f.NoEscopoAsync(async sp => await sp.GetRequiredService<ProgressoService>().SalvarAsync(id, 300, 3600));
        var servico = new PreAquecerContinuarService(f.Escopos, new ConfigurationBuilder().Build(), NullLogger<PreAquecerContinuarService>.Instance);
        return (f, id, servico);
    }

    private static async Task Espera(Func<bool> condicao, double segundos = 10)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicao() && DateTime.UtcNow < limite) await Task.Delay(50);
    }

    // ─── a política, isolada ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10_000L, 4_000L, 15_000L, true)]
    [InlineData(10_000L, 5_000L, 15_000L, true)]           // enche até o teto, sem passar
    [InlineData(10_000L, 5_001L, 15_000L, false)]          // um byte a mais expulsaria alguém
    [InlineData(0L, 4_800_000_000L, 15_000_000_000L, true)]
    [InlineData(14_000_000_000L, 4_800_000_000L, 15_000_000_000L, false)]
    [InlineData(999L, 999_999_999_999L, long.MaxValue, true)]   // cache sem teto
    public void Cabe_no_cache_so_se_nao_precisa_expulsar_nada(long noCache, long arquivo, long teto, bool cabe) =>
        Assert.Equal(cabe, PoliticaPreAquecimento.CabeNoCache(noCache, arquivo, teto));

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]    // alguém na fila esperando o encoder
    [InlineData(0, true, true)]     // placa quente
    [InlineData(2, true, true)]
    public void Pre_aquecimento_cede_o_lugar_a_quem_precisa(int esperando, bool quente, bool cede) =>
        Assert.Equal(cede, PoliticaPreAquecimento.DeveCeder(esperando, quente));

    // ─── o fluxo de verdade ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Aquece_o_remux_do_primeiro_do_continuar_assistindo()
    {
        var (f, id, servico) = await Cenario();

        Assert.Equal(id, await servico.ExecutarUmaVezAsync());

        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
        Assert.True(File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
    }

    [Fact]
    public async Task Sem_nada_pela_metade_aquece_o_proximo_episodio_da_serie_que_acabou_de_terminar()
    {
        var (f, _, servico) = await Cenario(comProgresso: false);
        f.Arquivo("Series/Show/Show.S01E01.mkv", new string('a', 4096));
        f.Arquivo("Series/Show/Show.S01E02.mkv", new string('b', 4096));
        await f.ScanAsync();
        var ep1 = await f.IdDeAsync("Series/Show/Show.S01E01.mkv");
        var ep2 = await f.IdDeAsync("Series/Show/Show.S01E02.mkv");
        await f.NoEscopoAsync(async sp => await sp.GetRequiredService<ProgressoService>().ConcluirAsync(ep1));

        Assert.Equal(ep2, await servico.ExecutarUmaVezAsync());

        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{ep2}.mp4")));
        Assert.True(File.Exists(Path.Combine(f.CacheRemux, $"{ep2}.mp4")));
    }

    [Fact]
    public async Task Terminado_o_primeiro_da_fila_aquece_o_segundo_na_checagem_seguinte()
    {
        var (f, idA, servico) = await Cenario();
        f.Arquivo("Filmes/Alien.1979.mkv", new string('y', 4096));
        await f.ScanAsync();
        var idB = await f.IdDeAsync("Filmes/Alien.1979.mkv");
        await f.NoEscopoAsync(async sp => await sp.GetRequiredService<ProgressoService>().SalvarAsync(idB, 200, 3600));
        var transcode = await f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<HlsTranscodeService>()));

        var primeiro = await servico.ExecutarUmaVezAsync();
        await Espera(() => transcode.Ocioso);
        var segundo = await servico.ExecutarUmaVezAsync();

        Assert.NotNull(primeiro);
        Assert.NotNull(segundo);
        Assert.NotEqual(primeiro, segundo);
        Assert.Equal(new[] { idA, idB }.Order(), new[] { primeiro!.Value, segundo!.Value }.Order());
    }

    [Fact]
    public async Task Loga_mb_segundos_e_mb_por_segundo_ao_fim_do_remux()
    {
        var (f, id, servico) = await Cenario();
        await servico.ExecutarUmaVezAsync();
        await Espera(() => f.Logs.Any(l => l.Contains("pronto:")));

        var linha = Assert.Single(f.Logs, l => l.Contains($"Remux do filme {id} pronto:"));

        Assert.StartsWith("Information:", linha);
        Assert.Contains(" MB em ", linha);
        Assert.Contains(" MB/s)", linha);
        Assert.Contains("pré-aquecido", linha);   // e diz que foi por palpite, não por pedido
    }

    [Fact]
    public async Task Nao_aquece_o_que_nao_precisa_de_remux()
    {
        // áudio AAC é aceito pelo navegador; em .mkv o arquivo não "toca cru" mas também não é caso de remux (vai por HLS)
        var (_, _, servico) = await Cenario(audio: "aac");
        Assert.Null(await servico.ExecutarUmaVezAsync());
    }

    [Fact]
    public async Task Sem_continuar_assistindo_nao_faz_nada()
    {
        var (_, _, servico) = await Cenario(comProgresso: false);
        Assert.Null(await servico.ExecutarUmaVezAsync());
    }

    [Fact]
    public async Task Nao_aquece_se_o_remux_nao_cabe_no_teto_do_cache()
    {
        // teto de ~1 KB: o arquivo (4 KB) não cabe sem expulsar nada
        var (f, _, servico) = await Cenario(config: new() { ["RemuxCacheMaxGB"] = "0.000001" });

        Assert.Null(await servico.ExecutarUmaVezAsync());

        Assert.Empty(Directory.EnumerateFiles(f.CacheRemux, "*.mp4"));
    }

    [Fact]
    public async Task Nao_aquece_com_o_servidor_ocupado()
    {
        var (f, idA, servico) = await Cenario(ffmpegSegundos: 1.5);
        f.Arquivo("Filmes/Alien.1979.mkv", new string('y', 4096));
        await f.ScanAsync();
        var idB = await f.IdDeAsync("Filmes/Alien.1979.mkv");

        // alguém pediu o remux do Alien de verdade: o encoder está em uso
        await f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<HlsTranscodeService>().ObterStatusRemux(idB, Path.Combine(f.Media, "Filmes/Alien.1979.mkv"))));

        Assert.Null(await servico.ExecutarUmaVezAsync());
        Assert.False(File.Exists(Path.Combine(f.CacheRemux, $"{idA}.mp4")));
    }

    [Fact]
    public async Task PreAquecerRemux_sozinho_tambem_recusa_com_o_encoder_em_uso()
    {
        // a trava vale no próprio serviço, não só na checagem de quem o chama
        var (f, idA, _) = await Cenario(ffmpegSegundos: 1.5);
        f.Arquivo("Filmes/Alien.1979.mkv", new string('y', 4096));
        await f.ScanAsync();
        var idB = await f.IdDeAsync("Filmes/Alien.1979.mkv");
        var transcode = await f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<HlsTranscodeService>()));
        transcode.ObterStatusRemux(idB, Path.Combine(f.Media, "Filmes/Alien.1979.mkv"));   // pedido de verdade em andamento

        Assert.False(transcode.Ocioso);
        Assert.False(transcode.PreAquecerRemux(idA, Path.Combine(f.Media, "Filmes/Matrix.1999.mkv")));
    }

    [Fact]
    public async Task Nao_aquece_de_novo_o_que_ja_esta_pronto()
    {
        var (f, id, servico) = await Cenario();
        await servico.ExecutarUmaVezAsync();
        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
        await Espera(() => f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<HlsTranscodeService>().Ocioso)).Result);

        Assert.Null(await servico.ExecutarUmaVezAsync());
    }

    [Fact]
    public async Task Aquecimento_nao_e_abortado_por_falta_de_poll_ao_contrario_de_um_remux_comum()
    {
        // o orfão normal mata em ~HlsOrphanTimeoutSeconds sem ninguém olhando; o aquecido não tem quem olhe, vai até o fim
        var (f, id, servico) = await Cenario(ffmpegSegundos: 4, config: new() { ["HlsOrphanTimeoutSeconds"] = "1" });

        await servico.ExecutarUmaVezAsync();
        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")), segundos: 15);

        Assert.True(File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
        Assert.DoesNotContain(f.Logs, l => l.Contains("abortado: ninguém pediu status"));
    }

    [Fact]
    public async Task Intervalo_zero_desliga_o_servico()
    {
        var (f, _, _) = await Cenario();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PreAquecerContinuarMinutos"] = "0" }).Build();
        var servico = new PreAquecerContinuarService(f.Escopos, config, NullLogger<PreAquecerContinuarService>.Instance);

        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await servico.StopAsync(CancellationToken.None);
    }
}
