using System.Diagnostics;

namespace FilmesApi.Services;

/// <summary>Roda um processo externo com timeout (e opcionalmente um detector de travamento),
/// matando a árvore de processos se estourar. Em qualquer encerramento forçado devolve o
/// stderr capturado até o momento — essencial pra diagnosticar ffmpeg que trava sem morrer.</summary>
public static class ProcessRunner
{
    /// <param name="psi">O processo a iniciar (stdout/stderr são configurados aqui).</param>
    /// <param name="timeout">Prazo máximo — depois disso a árvore de processos é morta.</param>
    /// <param name="travou">Chamado a cada ~2s enquanto o processo roda; se devolver true,
    /// o processo é considerado travado (sem progresso) e morto. Null = só o timeout vale.</param>
    /// <param name="ct">Cancelado no shutdown do host — mata a árvore de processos em vez de
    /// deixar o ffmpeg como órfão até o SIGKILL do container.</param>
    public static async Task<(int ExitCode, string Stderr)> ExecutarComTimeoutAsync(
        ProcessStartInfo psi, TimeSpan timeout, Func<bool>? travou = null, CancellationToken ct = default)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardError = true;

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "não foi possível iniciar o processo.");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        var prazo = DateTime.UtcNow + timeout;

        while (!proc.HasExited)
        {
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException)
            {
                Matar(proc);
                return (-1, "host encerrando — processo abortado.\n" +
                            $"--- stderr até aqui ---\n{await StderrParcial(stderrTask)}");
            }
            if (proc.HasExited) break;

            if (DateTime.UtcNow > prazo)
            {
                Matar(proc);
                return (-1, $"processo excedeu o timeout de {timeout} e foi encerrado.\n" +
                            $"--- stderr até aqui ---\n{await StderrParcial(stderrTask)}");
            }
            if (travou is not null && travou())
            {
                Matar(proc);
                return (-1, "processo travou (sem progresso) e foi encerrado.\n" +
                            $"--- stderr até aqui ---\n{await StderrParcial(stderrTask)}");
            }
        }

        return (proc.ExitCode, await stderrTask);
    }

    private static void Matar(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* já morreu / sem permissão */ }
    }

    private static async Task<string> StderrParcial(Task<string> stderrTask)
    {
        var vencedor = await Task.WhenAny(stderrTask, Task.Delay(2000));
        return vencedor == stderrTask ? stderrTask.Result : "(stderr não veio a tempo)";
    }
}
