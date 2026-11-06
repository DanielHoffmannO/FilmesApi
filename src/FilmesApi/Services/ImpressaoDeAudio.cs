using System.Diagnostics;

namespace FilmesApi.Services;

/// <summary>Impressão de áudio (chromaprint, via ffmpeg) do começo de um arquivo: a entrada de <see cref="DetectorDeAbertura"/>.</summary>
public static class ImpressaoDeAudio
{
    /// <summary>Quanto do começo do episódio se analisa: a abertura cabe aí com folga e o resto do arquivo nem é lido.</summary>
    public const int JanelaSegundos = 480;

    /// <summary>Quanto do FIM do episódio se analisa (os créditos).</summary>
    public const int JanelaFimSegundos = 300;

    /// <summary>Que pedaço do arquivo ler: <c>InicioSegundos</c> (-ss; null = do começo) e <c>DuracaoSegundos</c> (-t), ou os últimos
    /// <c>DuracaoSegundos</c> do arquivo (<c>DoFim</c>, -sseof).</summary>
    public record Trecho(double? InicioSegundos, double DuracaoSegundos, bool DoFim = false)
    {
        public static Trecho Inicio => new(null, JanelaSegundos);
        public static Trecho Fim => new(null, JanelaFimSegundos, DoFim: true);
    }

    /// <summary>Os inteiros da impressão, ou null se o ffmpeg falhou (sem chromaprint no build, sem áudio, arquivo ilegível).
    /// Cancelamento relança. Roda em prioridade baixa pra não atrapalhar a TV.</summary>
    public static Task<int[]?> ObterAsync(string ffmpeg, string arquivo, CancellationToken ct, int timeoutSegundos = 120) =>
        ObterAsync(ffmpeg, arquivo, Trecho.Inicio, ct, timeoutSegundos);

    public static async Task<int[]?> ObterAsync(string ffmpeg, string arquivo, Trecho trecho, CancellationToken ct, int timeoutSegundos = 120)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (trecho.DoFim) args.AddRange(["-sseof", (-trecho.DuracaoSegundos).ToString("0.###", inv)]);
        else if (trecho.InicioSegundos is double ss && ss > 0) args.AddRange(["-ss", ss.ToString("0.###", inv)]);
        if (!trecho.DoFim) args.AddRange(["-t", trecho.DuracaoSegundos.ToString("0.###", inv)]);
        args.AddRange(["-i", arquivo, "-map", "0:a:0", "-vn", "-ac", "1", "-f", "chromaprint", "-fp_format", "raw", "-"]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* sem permissão: segue na normal */ }
            _ = proc.StandardError.ReadToEndAsync(ct);

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(timeoutSegundos));
            using var bytes = new MemoryStream();
            try
            {
                await proc.StandardOutput.BaseStream.CopyToAsync(bytes, limite.Token);
                await proc.WaitForExitAsync(limite.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* já morreu */ }
                if (ct.IsCancellationRequested) throw;
                return null;   // estourou o prazo
            }

            if (proc.ExitCode != 0 || bytes.Length < 4) return null;
            var buf = bytes.GetBuffer();
            var n = (int)bytes.Length / 4;
            var itens = new int[n];
            for (var i = 0; i < n; i++) itens[i] = BitConverter.ToInt32(buf, i * 4);
            return itens;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // ffmpeg não encontrado
    }
}
