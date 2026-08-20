using System.Diagnostics;

namespace FilmesApi.Services;

/// <summary>Roda um processo externo com timeout, matando a árvore de processos se estourar.</summary>
public static class ProcessRunner
{
    public static async Task<(int ExitCode, string Stderr)> ExecutarComTimeoutAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardError = true;

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "não foi possível iniciar o processo.");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            return (-1, $"processo excedeu o timeout de {timeout} e foi encerrado.");
        }

        return (proc.ExitCode, await stderrTask);
    }
}
