using System.Diagnostics;
using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão: cancelar o token durante um processo em andamento (shutdown do host) TEM que
/// propagar como <see cref="OperationCanceledException"/>, não devolver uma tupla de "falha"
/// comum — senão um shutdown limpo cai na mesma cascata de retry/fallback de uma falha real
/// de ffmpeg (ver <c>HlsTranscodeService.TranscodificarHlsAsync</c>: o catch de
/// OperationCanceledException não marca <c>_falhas</c> nem desliga o rkmpp; o catch genérico
/// de Exception marca os dois). Usa processos reais (sleep/sh), sem ffmpeg — mais rápido e
/// não depende do binário estar instalado.
/// </summary>
public class ProcessRunnerTests
{
    [Fact]
    public async Task Cancelamento_no_meio_do_processo_lanca_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var psi = new ProcessStartInfo("sleep", "10");

        var tarefa = ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(5), null, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarefa);
    }

    [Fact]
    public async Task Cancelamento_mata_o_processo_nao_deixa_orfao_rodando()
    {
        using var cts = new CancellationTokenSource();
        var psi = new ProcessStartInfo("sleep", "10");
        var cronometro = Stopwatch.StartNew();

        var tarefa = ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(5), null, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        try { await tarefa; } catch (OperationCanceledException) { /* esperado */ }
        cronometro.Stop();

        // ExecutarComTimeoutAsync só retorna quando o processo já saiu (proc.HasExited). Se
        // Matar() não tivesse matado de verdade (Kill(entireProcessTree:true)), o "sleep 10"
        // seguiria rodando órfão e este await só retornaria (ou nunca, já que ninguém mais
        // espera por ele) perto dos 10s. Bem abaixo disso confirma a morte real.
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(5),
            $"levou {cronometro.Elapsed} pra retornar — processo pode não ter sido morto de verdade");
    }

    [Fact]
    public async Task Processo_normal_devolve_exit_code_e_stderr()
    {
        var psi = new ProcessStartInfo("sh", "-c \"echo deu-erro 1>&2; exit 3\"");
        var (exitCode, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromSeconds(10));

        Assert.Equal(3, exitCode);
        Assert.Contains("deu-erro", stderr);
    }

    [Fact]
    public async Task Callback_travou_mata_o_processo_e_devolve_exit_code_negativo()
    {
        var psi = new ProcessStartInfo("sleep", "10");
        var (exitCode, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(
            psi, TimeSpan.FromMinutes(5), travou: () => true);

        Assert.Equal(-1, exitCode);
        Assert.Contains("travou", stderr);
    }
}
