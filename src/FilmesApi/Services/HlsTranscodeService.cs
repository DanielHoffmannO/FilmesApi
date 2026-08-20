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
    private readonly string _cachePathLegado;
    private readonly TimeSpan _jobTimeout;
    private readonly SemaphoreSlim _slotEncoder;
    private readonly RkmppCapabilityService _rkmpp;
    private readonly ILogger<HlsTranscodeService> _logger;

    private readonly ConcurrentDictionary<int, Task> _jobs = new();
    private readonly ConcurrentDictionary<int, bool> _falhas = new();
    private readonly ConcurrentDictionary<int, bool> _completos = new();
    private readonly object _decisaoLock = new();

    public HlsTranscodeService(IConfiguration config, RkmppCapabilityService rkmpp, ILogger<HlsTranscodeService> logger)
    {
        _cachePath = config.GetValue<string>("HlsCachePath") ?? "/data/hls";
        _ffmpegPath = config.GetValue<string>("FfmpegPath") ?? "ffmpeg";
        _ffprobePath = config.GetValue<string>("FfprobePath") ?? "ffprobe";
        _cachePathLegado = config.GetValue<string>("TranscodeCachePath") ?? "/data/transcoded";
        var maxJobs = config.GetValue<int?>("MaxConcurrentTranscodeJobs") ?? 1;
        _jobTimeout = TimeSpan.FromHours(config.GetValue<double?>("TranscodeJobTimeoutHours") ?? 6);
        _slotEncoder = new SemaphoreSlim(maxJobs, maxJobs);
        _rkmpp = rkmpp;
        _logger = logger;
        Directory.CreateDirectory(_cachePath);
    }

    public string DiretorioCache(int filmeId) => Path.Combine(_cachePath, filmeId.ToString());
    public string CaminhoPlaylist(int filmeId) => Path.Combine(DiretorioCache(filmeId), "playlist.m3u8");

    /// <summary>Apaga best-effort qualquer cache de transcode do filme (HLS atual e .mp4
    /// legado de uma geração anterior) — usado ao deletar o filme do catálogo.</summary>
    public void LimparCache(int filmeId)
    {
        try
        {
            lock (_decisaoLock)
            {
                var dir = DiretorioCache(filmeId);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }

            var mp4Legado = Path.Combine(_cachePathLegado, $"{filmeId}.mp4");
            if (File.Exists(mp4Legado)) File.Delete(mp4Legado);
        }
        catch (IOException) { /* best-effort: não bloqueia a exclusão do filme */ }
        catch (UnauthorizedAccessException) { /* best-effort: não bloqueia a exclusão do filme */ }
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

            // Uma vez confirmado completo, nunca mais muda — evita reler o playlist.m3u8
            // inteiro do disco (que cresce com o número de segments) em toda chamada futura,
            // inclusive nas próximas vezes que alguém reabrir o mesmo filme já cacheado.
            if (_completos.ContainsKey(filmeId))
                return (StreamStatus.Disponivel, playlist);

            if (File.Exists(playlist) && File.ReadAllText(playlist).Contains("#EXT-X-ENDLIST"))
            {
                _completos[filmeId] = true;
                return (StreamStatus.Disponivel, playlist);
            }

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
            // Só o codec de vídeo decide o caminho de encode aqui — não precisa do de áudio
            // (que ProbeCodecsAsync também traria), então evita o segundo processo ffprobe.
            var videoCodec = await RunFfprobeAsync(origem, "v:0", CancellationToken.None);
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
                    // Segments já gerados pelo rkmpp podem já ter sido servidos a quem estava
                    // assistindo — apagar/recriar precisa do mesmo lock que protege as
                    // leituras de ObterStatusAsync, senão uma checagem concorrente pode ler o
                    // diretório no meio da troca.
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
        List<string> args = ["-y", "-i", origem];

        if (videoCompativel)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            if (usarRkmpp)
                args.AddRange(["-init_hw_device", "rkmpp=rk", "-filter_hw_device", "rk", "-vf", "format=nv12,hwupload", "-c:v", "h264_rkmpp"]);
            else
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23"]);

            // Segmentação previsível mesmo com fps/keyframes irregulares da fonte — vale
            // pros dois encoders de reencode, só não faz sentido no remux acima (-c:v copy
            // só pode cortar em keyframes que já existem no arquivo original).
            args.AddRange(["-sc_threshold", "0", "-force_key_frames", $"expr:gte(t,n_forced*{SegmentoSegundos})"]);
        }

        args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        args.AddRange([
            "-f", "hls",
            "-hls_time", SegmentoSegundos.ToString(),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_flags", "temp_file+independent_segments",
            "-hls_segment_type", "mpegts",
            "-start_number", "0",
            "-hls_segment_filename", "seg_%05d.ts",
            "playlist.m3u8",
        ]);

        var psi = new ProcessStartInfo(_ffmpegPath) { WorkingDirectory = dir };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        return await ProcessRunner.ExecutarComTimeoutAsync(psi, _jobTimeout);
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
        var videoTask = RunFfprobeAsync(path, "v:0", ct);
        var audioTask = RunFfprobeAsync(path, "a:0", ct);
        await Task.WhenAll(videoTask, audioTask);
        return (videoTask.Result, audioTask.Result);
    }

    private async Task<string?> RunFfprobeAsync(string path, string stream, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobePath) { RedirectStandardOutput = true };
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
