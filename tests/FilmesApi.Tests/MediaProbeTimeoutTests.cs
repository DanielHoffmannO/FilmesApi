using System.Diagnostics;
using FilmesApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão: ao estourar o timeout, o ffprobe pendurado (arquivo num mount lento/travado é
/// o caso real) tinha que ficar rodando sozinho — <c>using var proc</c> só solta o handle do
/// .NET, não mata o processo do SO. Cada timeout empilhava mais um zumbi. Usa <c>sleep</c> no
/// lugar do ffprobe (aponta <c>FfmpegOptions.Ffprobe</c> pra ele) — mais rápido e não depende
/// do binário estar instalado.
/// </summary>
public class MediaProbeTimeoutTests
{
    [Fact]
    public async Task Timeout_mata_o_processo_nao_deixa_orfao_rodando()
    {
        var probe = new MediaProbeService(new FfmpegOptions("ffmpeg-nao-usado", "sleep"), NullLogger<MediaProbeService>.Instance);
        var cronometro = Stopwatch.StartNew();

        var resultado = await probe.RodarAsync(["10"], TimeSpan.FromMilliseconds(300), "caminho-fake", CancellationToken.None);

        cronometro.Stop();
        Assert.Null(resultado);  // timeout -> devolve null
        // Se o "sleep 10" não tivesse sido morto, ficaria rodando órfão por 10s — bem abaixo
        // disso confirma que Kill(entireProcessTree:true) matou de verdade.
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(5),
            $"levou {cronometro.Elapsed} — processo pode não ter sido morto de verdade");
    }

    [Fact]
    public async Task Processo_que_termina_a_tempo_devolve_a_saida()
    {
        var probe = new MediaProbeService(new FfmpegOptions("ffmpeg-nao-usado", "echo"), NullLogger<MediaProbeService>.Instance);

        var resultado = await probe.RodarAsync(["ola"], TimeSpan.FromSeconds(10), "caminho-fake", CancellationToken.None);

        Assert.Equal("ola\n", resultado);
    }
}
