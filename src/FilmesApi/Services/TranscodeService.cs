using System.Collections.Concurrent;
using System.Diagnostics;

namespace FilmesApi.Services;

public enum StreamStatus { Compativel, Pronto, Convertendo, Erro }

/// <summary>
/// Garante que o vídeo tocável no navegador: se o codec original já é suportado, serve
/// direto; senão remux/transcodifica uma vez via ffmpeg e guarda em cache.
/// </summary>
public class TranscodeService
{
    private static readonly string[] VideoCodecsCompativeis = ["h264", "vp9", "av1"];
    private static readonly string[] AudioCodecsCompativeis = ["aac", "mp3", "opus"];

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<int, Task> _jobsEmAndamento = new();
    private readonly ConcurrentDictionary<int, bool> _falhas = new();

    public TranscodeService(IConfiguration config)
    {
        _cachePath = config.GetValue<string>("TranscodeCachePath") ?? "/data/transcoded";
        Directory.CreateDirectory(_cachePath);
    }

    private string CaminhoCache(int filmeId) => Path.Combine(_cachePath, $"{filmeId}.mp4");

    /// <summary>Retorna o status atual e, quando aplicável, o caminho absoluto pronto para servir.</summary>
    public async Task<(StreamStatus Status, string? Path)> ObterStatusAsync(int filmeId, string arquivoOriginal, CancellationToken ct)
    {
        if (File.Exists(CaminhoCache(filmeId)))
            return (StreamStatus.Pronto, CaminhoCache(filmeId));

        if (_falhas.ContainsKey(filmeId))
            return (StreamStatus.Erro, null);

        if (await EhCompativelAsync(arquivoOriginal, ct))
            return (StreamStatus.Compativel, arquivoOriginal);

        _ = _jobsEmAndamento.GetOrAdd(filmeId, _ => Task.Run(() => TranscodificarAsync(filmeId, arquivoOriginal), CancellationToken.None));
        return (StreamStatus.Convertendo, null);
    }

    private async Task<bool> EhCompativelAsync(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mp4" or ".webm" or ".mov" or ".m4v")) return false;

        var (video, audio) = await ProbeCodecsAsync(path, ct);
        var videoOk = video is not null && VideoCodecsCompativeis.Contains(video);
        var audioOk = audio is null || AudioCodecsCompativeis.Contains(audio);
        return videoOk && audioOk;
    }

    private async Task<(string? Video, string? Audio)> ProbeCodecsAsync(string path, CancellationToken ct)
    {
        var video = await RunFfprobeAsync(path, "v:0", ct);
        var audio = await RunFfprobeAsync(path, "a:0", ct);
        return (video, audio);
    }

    private static async Task<string?> RunFfprobeAsync(string path, string stream, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add(stream);
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_name");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var saida = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var codec = saida.Trim().Split('\n')[0].Trim();
        return string.IsNullOrWhiteSpace(codec) ? null : codec;
    }

    private async Task TranscodificarAsync(int filmeId, string origem)
    {
        var destino = CaminhoCache(filmeId);
        var temp = destino + ".tmp";
        try
        {
            var (videoCodec, _) = await ProbeCodecsAsync(origem, CancellationToken.None);
            var videoCompativel = videoCodec is not null && VideoCodecsCompativeis.Contains(videoCodec);

            var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(origem);
            if (videoCompativel)
            {
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("copy");
            }
            else
            {
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add("23");
            }
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a");
            psi.ArgumentList.Add("192k");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(temp);

            using var proc = Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0 && File.Exists(temp))
                File.Move(temp, destino, overwrite: true);
            else
                throw new InvalidOperationException($"ffmpeg saiu com código {proc.ExitCode}.");
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            _falhas[filmeId] = true;
        }
        finally
        {
            _jobsEmAndamento.TryRemove(filmeId, out _);
        }
    }
}
