using FilmesApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Abrir um filme dispara /legendas, /audios e o HLS quase juntos, todos pedindo o probe do
/// mesmo arquivo com o cache frio — tinha que ser UM ffprobe, não um por pedido (a placa é fraca).
/// Usa um "ffprobe" falso (script) que conta as chamadas e demora o bastante pra elas se sobreporem.</summary>
public class MediaProbeCacheTests : IDisposable
{
    private readonly string _dir = TestesComuns.CriarPastaTemp();
    private readonly string _video;
    private readonly string _contador;
    private readonly MediaProbeService _probe;

    public MediaProbeCacheTests()
    {
        _video = Path.Combine(_dir, "filme.mkv");
        File.WriteAllText(_video, "conteudo");
        _contador = Path.Combine(_dir, "chamadas.txt");

        var script = TestesComuns.ScriptFalso(_dir, "ffprobe-falso.sh", $$$"""
            echo x >> "{{{_contador}}}"
            sleep 0.4
            echo '{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720}],"format":{"duration":"60.0"}}'
            """);

        _probe = new MediaProbeService(new FfmpegOptions("ffmpeg-nao-usado", script), NullLogger<MediaProbeService>.Instance);
    }

    public void Dispose()
    {
        _probe.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private int Chamadas => File.Exists(_contador) ? File.ReadAllLines(_contador).Length : 0;

    [Fact]
    public async Task Pedidos_simultaneos_dividem_um_ffprobe_so()
    {
        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _probe.InspecionarAsync(_video, CancellationToken.None)));

        Assert.All(resultados, r => Assert.Equal("h264", r?.VideoCodec));
        Assert.Equal(1, Chamadas);
    }

    [Fact]
    public async Task Depois_de_lido_vem_do_cache()
    {
        await _probe.InspecionarAsync(_video, CancellationToken.None);
        var segundo = await _probe.InspecionarAsync(_video, CancellationToken.None);

        Assert.Equal(60.0, segundo?.DuracaoSegundos);
        Assert.Equal(1, Chamadas);
    }

    [Fact]
    public async Task Arquivo_alterado_e_lido_de_novo()
    {
        await _probe.InspecionarAsync(_video, CancellationToken.None);
        File.WriteAllText(_video, "conteudo bem maior que o anterior");

        await _probe.InspecionarAsync(_video, CancellationToken.None);

        Assert.Equal(2, Chamadas);
    }

    [Fact]
    public async Task Cancelar_a_espera_de_um_nao_derruba_a_leitura_dos_outros()
    {
        using var cts = new CancellationTokenSource();
        var desistente = _probe.InspecionarAsync(_video, cts.Token);
        var outro = _probe.InspecionarAsync(_video, CancellationToken.None);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => desistente);
        Assert.Equal("h264", (await outro)?.VideoCodec);
        Assert.Equal(1, Chamadas);
    }

    [Fact]
    public async Task Arquivo_inexistente_devolve_null_sem_rodar_ffprobe()
    {
        Assert.Null(await _probe.InspecionarAsync(Path.Combine(_dir, "nao-existe.mkv"), CancellationToken.None));
        Assert.Equal(0, Chamadas);
    }
}
