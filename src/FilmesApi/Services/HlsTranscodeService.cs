using System.Collections.Concurrent;
using System.Diagnostics;

namespace FilmesApi.Services;

public enum StreamStatus { Compativel, Preparando, Disponivel, Erro }

/// <summary>
/// Garante que o vídeo seja tocável no navegador: se o codec original já é suportado,
/// serve direto; senão gera HLS incrementalmente (remux por stream-copy quando só o
/// container é o problema, ou reencode via rkmpp/libx264 quando o vídeo também precisa
/// mudar), guardando os segments em cache permanente por filme. O play começa assim que
/// o primeiro segment existe, sem esperar o filme inteiro terminar de converter.
/// </summary>
public class HlsTranscodeService
{
    private const int SegmentoSegundos = 6;

    private static readonly string[] VideoCodecsCompativeis = ["h264", "vp9", "av1"];
    private static readonly string[] AudioCodecsCompativeis = ["aac", "mp3", "opus"];

    private readonly string _cachePath;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly TimeSpan _jobTimeout;
    private readonly SemaphoreSlim _slotEncoder;
    private readonly RkmppCapabilityService _rkmpp;
    private readonly ILogger<HlsTranscodeService> _logger;

    private readonly ConcurrentDictionary<int, Task> _jobs = new();
    private readonly ConcurrentDictionary<int, bool> _falhas = new();
    private readonly object _decisaoLock = new();

    public HlsTranscodeService(IConfiguration config, RkmppCapabilityService rkmpp, ILogger<HlsTranscodeService> logger)
    {
        _cachePath = config.GetValue<string>("HlsCachePath") ?? "/data/hls";
        _ffmpegPath = config.GetValue<string>("FfmpegPath") ?? "ffmpeg";
        _ffprobePath = config.GetValue<string>("FfprobePath") ?? "ffprobe";
        var maxJobs = config.GetValue<int?>("MaxConcurrentTranscodeJobs") ?? 1;
        _jobTimeout = TimeSpan.FromHours(config.GetValue<double?>("TranscodeJobTimeoutHours") ?? 6);
        _slotEncoder = new SemaphoreSlim(maxJobs, maxJobs);
        _rkmpp = rkmpp;
        _logger = logger;
        Directory.CreateDirectory(_cachePath);
    }

    public string DiretorioCache(int filmeId) => Path.Combine(_cachePath, filmeId.ToString());
    public string CaminhoPlaylist(int filmeId) => Path.Combine(DiretorioCache(filmeId), "playlist.m3u8");

    /// <summary>Apaga o cache de um filme (usado ao deletar o filme do catálogo).</summary>
    public void LimparCache(int filmeId)
    {
        lock (_decisaoLock)
        {
            var dir = DiretorioCache(filmeId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Retorna o status atual e, quando aplicável, o caminho pronto para servir
    /// (arquivo original se compatível, ou playlist.m3u8 se HLS já tem algo pronto).</summary>
    public async Task<(StreamStatus Status, string? Path)> ObterStatusAsync(int filmeId, string arquivoOriginal, CancellationToken ct)
    {
        if (await EhCompativelAsync(arquivoOriginal, ct))
            return (StreamStatus.Compativel, arquivoOriginal);

        var dir = DiretorioCache(filmeId);
        var playlist = CaminhoPlaylist(filmeId);

        // Toda a decisão (falha permanente / cache completo / job em andamento / começar do
        // zero) precisa ser uma única seção síncrona e atômica sob o mesmo lock que protege
        // as mutações do job (TranscodificarHlsAsync) — senão duas requisições concorrentes
        // podem, por exemplo, ler "sem job, sem ENDLIST" bem no instante em que o job estava
        // terminando com sucesso, e apagar um cache recém-completo pra recomeçar à toa.
        lock (_decisaoLock)
        {
            if (_falhas.ContainsKey(filmeId))
                return (StreamStatus.Erro, null);

            if (File.Exists(playlist) && File.ReadAllText(playlist).Contains("#EXT-X-ENDLIST"))
                return (StreamStatus.Disponivel, playlist);

            if (_jobs.ContainsKey(filmeId))
            {
                var temSegmento = TemSegmento(dir);
                return (temSegmento ? StreamStatus.Disponivel : StreamStatus.Preparando, temSegmento ? playlist : null);
            }

            // Sem job vivo neste processo e sem cache completo: uma playlist parcial
            // encontrada aqui só pode ser resto de um job interrompido (crash/restart) —
            // não é confiável, então começa do zero.
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            _jobs[filmeId] = Task.Run(() => TranscodificarHlsAsync(filmeId, arquivoOriginal), CancellationToken.None);
            return (StreamStatus.Preparando, null);
        }
    }

    private static bool TemSegmento(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "seg_*.ts").Any();
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private async Task TranscodificarHlsAsync(int filmeId, string origem)
    {
        var dir = DiretorioCache(filmeId);
        await _slotEncoder.WaitAsync(CancellationToken.None);
        try
        {
            var (videoCodec, _) = await ProbeCodecsAsync(origem, CancellationToken.None);
            var videoCompativel = videoCodec is not null && VideoCodecsCompativeis.Contains(videoCodec);
            var usarRkmpp = !videoCompativel && await _rkmpp.DisponivelAsync();

            var (exitCode, stderr) = await RunFfmpegHlsAsync(origem, dir, videoCompativel, usarRkmpp);

            if (usarRkmpp)
            {
                _rkmpp.RegistrarResultado(exitCode == 0);
                if (exitCode != 0)
                {
                    _logger.LogWarning("Encode via rkmpp falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando de novo com libx264.",
                        filmeId, exitCode, stderr);
                    // Segments já gerados pelo rkmpp podem já ter sido servidos (com cache
                    // "immutable") a quem estava assistindo — apagar/recriar precisa do
                    // mesmo lock que protege as leituras de ObterStatusAsync, senão uma
                    // checagem concorrente pode ler o diretório no meio da troca.
                    lock (_decisaoLock)
                    {
                        Directory.Delete(dir, recursive: true);
                        Directory.CreateDirectory(dir);
                    }
                    (exitCode, stderr) = await RunFfmpegHlsAsync(origem, dir, videoCompativel, usarRkmpp: false);
                }
            }

            if (exitCode != 0)
                throw new InvalidOperationException($"ffmpeg saiu com código {exitCode}: {stderr}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao transcodificar filme {Id} para HLS.", filmeId);
            lock (_decisaoLock)
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                _falhas[filmeId] = true;
            }
        }
        finally
        {
            lock (_decisaoLock) { _jobs.TryRemove(filmeId, out _); }
            _slotEncoder.Release();
        }
    }

    private async Task<(int ExitCode, string Stderr)> RunFfmpegHlsAsync(
        string origem, string dir, bool videoCompativel, bool usarRkmpp)
    {
        var psi = new ProcessStartInfo(_ffmpegPath) { UseShellExecute = false, RedirectStandardError = true, WorkingDirectory = dir };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(origem);

        if (videoCompativel)
        {
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("copy");
        }
        else if (usarRkmpp)
        {
            psi.ArgumentList.Add("-init_hw_device");
            psi.ArgumentList.Add("rkmpp=rk");
            psi.ArgumentList.Add("-filter_hw_device");
            psi.ArgumentList.Add("rk");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add("format=nv12,hwupload");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("h264_rkmpp");
            psi.ArgumentList.Add("-sc_threshold");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-force_key_frames");
            psi.ArgumentList.Add($"expr:gte(t,n_forced*{SegmentoSegundos})");
        }
        else
        {
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add("23");
            psi.ArgumentList.Add("-sc_threshold");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-force_key_frames");
            psi.ArgumentList.Add($"expr:gte(t,n_forced*{SegmentoSegundos})");
        }

        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("192k");

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("hls");
        psi.ArgumentList.Add("-hls_time");
        psi.ArgumentList.Add(SegmentoSegundos.ToString());
        psi.ArgumentList.Add("-hls_list_size");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-hls_playlist_type");
        psi.ArgumentList.Add("event");
        psi.ArgumentList.Add("-hls_flags");
        psi.ArgumentList.Add("temp_file+independent_segments");
        psi.ArgumentList.Add("-hls_segment_type");
        psi.ArgumentList.Add("mpegts");
        psi.ArgumentList.Add("-start_number");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-hls_segment_filename");
        psi.ArgumentList.Add("seg_%05d.ts");
        psi.ArgumentList.Add("playlist.m3u8");

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "não foi possível iniciar o ffmpeg.");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(_jobTimeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            // Sem timeout aqui, um ffmpeg travado ocuparia o único slot do semáforo pra
            // sempre, travando a transcodificação de qualquer outro filme indefinidamente.
            return (-1, $"ffmpeg excedeu o timeout de {_jobTimeout} e foi encerrado.");
        }

        var stderr = await stderrTask;
        return (proc.ExitCode, stderr);
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

    private async Task<string?> RunFfprobeAsync(string path, string stream, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
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
}
