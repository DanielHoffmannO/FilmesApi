using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

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
    private static readonly string[] IdiomasAudioPreferidos = ["por", "pt", "pob"];

    /// <summary>Nome do marcador que sinaliza "esse cache é só remux stream-copy" — pura
    /// duplicata do original, sem ganho de compressão, então é o primeiro a ser despejado.</summary>
    private const string MarcadorRemux = ".remux";

    private readonly string _cachePath;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly string _cachePathLegado;
    private readonly TimeSpan _jobTimeout;
    private readonly TimeSpan _stallTimeout;
    private readonly long _cacheMaxBytes;
    private readonly int _alturaMaxReencode;
    private readonly bool _rkmppDecodeHw;
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
        _stallTimeout = TimeSpan.FromMinutes(config.GetValue<double?>("HlsStallTimeoutMinutes") ?? 8);
        var maxGb = config.GetValue<double?>("HlsCacheMaxGB") ?? 20;
        _cacheMaxBytes = maxGb > 0 ? (long)(maxGb * 1024 * 1024 * 1024) : long.MaxValue;
        _alturaMaxReencode = config.GetValue<int?>("HlsMaxAlturaReencode") ?? 1080;  // 0 = sem downscale
        // Decode via VPU no caminho rkmpp (só pros 4K com downscale). Off por padrão:
        // depende de scale_rkrga estar no ffmpeg e de o device aceitar drm_prime — o
        // agente do servidor liga isso só depois de validar por linha de comando.
        _rkmppDecodeHw = config.GetValue<bool?>("HlsRkmppDecodeHw") ?? false;
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

    /// <summary>Marca "assistido agora" tocando o mtime do playlist — sinal de LRU pra
    /// eviction (sobrevive a restart, ao contrário de um dicionário em memória).
    /// Throttle: só escreve se o mtime já está velho, pra não martelar disco no poll de status.</summary>
    private static void RegistrarAcesso(string playlist)
    {
        try
        {
            if (!File.Exists(playlist)) return;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(playlist) > TimeSpan.FromMinutes(2))
                File.SetLastWriteTimeUtc(playlist, DateTime.UtcNow);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Se o cache HLS total passou do teto (<c>HlsCacheMaxGB</c>), despeja diretórios até
    /// voltar pra ~90% do teto. Ordem de despejo: primeiro os remux stream-copy (pura
    /// duplicata do original), depois por acesso mais antigo (LRU). Nunca despeja um cache
    /// com job vivo nem um acessado nos últimos 30 min (alguém provavelmente assistindo).
    /// </summary>
    public void LimparCacheExcedente()
    {
        if (_cacheMaxBytes == long.MaxValue) return;

        try
        {
            if (!Directory.Exists(_cachePath)) return;

            var caches = new List<(int Id, string Dir, long Bytes, DateTime Acesso, bool Remux)>();
            long total = 0;
            foreach (var dir in Directory.EnumerateDirectories(_cachePath))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var id)) continue;
                long bytes = 0;
                DateTime acesso = Directory.GetLastWriteTimeUtc(dir);
                foreach (var arq in Directory.EnumerateFiles(dir))
                {
                    var fi = new FileInfo(arq);
                    bytes += fi.Length;
                    if (fi.Name == "playlist.m3u8") acesso = fi.LastWriteTimeUtc;
                }
                total += bytes;
                caches.Add((id, dir, bytes, acesso, File.Exists(Path.Combine(dir, MarcadorRemux))));
            }

            if (total <= _cacheMaxBytes) return;

            var alvo = (long)(_cacheMaxBytes * 0.9);
            var corte = DateTime.UtcNow - TimeSpan.FromMinutes(30);
            var candidatos = caches
                .Where(c => c.Acesso < corte && !_jobs.ContainsKey(c.Id))
                .OrderByDescending(c => c.Remux)     // remux (duplicata) sai primeiro
                .ThenBy(c => c.Acesso)               // depois, menos recentemente assistido
                .ToList();

            foreach (var c in candidatos)
            {
                if (total <= alvo) break;
                lock (_decisaoLock)
                {
                    if (_jobs.ContainsKey(c.Id)) continue;
                    try { Directory.Delete(c.Dir, recursive: true); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    _completos.TryRemove(c.Id, out _);
                    _falhas.TryRemove(c.Id, out _);
                }
                total -= c.Bytes;
                _logger.LogInformation(
                    "Cache HLS acima do teto: despejando filme {Id} ({Mb} MB, {Tipo}, último acesso {Acesso:u}).",
                    c.Id, c.Bytes / 1024 / 1024, c.Remux ? "remux" : "reencode", c.Acesso);
            }

            if (total > _cacheMaxBytes)
                _logger.LogWarning("Cache HLS ainda acima do teto ({Mb} MB) — todos os caches restantes " +
                    "estão em uso ou foram acessados há pouco. Nada despejado.", total / 1024 / 1024);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao avaliar/despejar cache HLS excedente.");
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

            // Uma vez confirmado completo, nunca mais muda — evita reler o playlist.m3u8
            // inteiro do disco (que cresce com o número de segments) em toda chamada futura,
            // inclusive nas próximas vezes que alguém reabrir o mesmo filme já cacheado.
            if (_completos.ContainsKey(filmeId))
            {
                RegistrarAcesso(playlist);
                return (StreamStatus.Disponivel, playlist);
            }

            if (File.Exists(playlist) && File.ReadAllText(playlist).Contains("#EXT-X-ENDLIST"))
            {
                _completos[filmeId] = true;
                RegistrarAcesso(playlist);
                return (StreamStatus.Disponivel, playlist);
            }

            if (_jobs.ContainsKey(filmeId))
            {
                var temSegmento = TemSegmento(dir);
                if (temSegmento) RegistrarAcesso(playlist);
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

            // 4K/UHD decodificado + encodado em software trava o Radxa por minutos e esquenta
            // a placa. Se vamos reencodar e a entrada passa do teto, reduz a resolução no
            // filtro do ffmpeg — corta o custo de encode (e a chance de cair no fallback
            // libx264) drasticamente. Perde-se resolução, ganha-se estabilidade.
            int? downscalePara = null;
            if (!videoCompativel && _alturaMaxReencode > 0)
            {
                var res = await ProbeResolucaoAsync(origem, CancellationToken.None);
                if (res is { } r && r.Altura > _alturaMaxReencode)
                {
                    downscalePara = _alturaMaxReencode;
                    _logger.LogWarning(
                        "Filme {Id}: entrada {W}x{H} — reencode com downscale pra {Alvo}p (4K+ em software sobrecarrega o servidor).",
                        filmeId, r.Largura, r.Altura, _alturaMaxReencode);
                }
            }

            var usarRkmpp = !videoCompativel && await _rkmpp.DisponivelAsync();
            var audioStreamIndex = await EscolherStreamAudioAsync(origem, CancellationToken.None);

            // Decode por hardware só faz diferença (e só vale o risco) no 4K com downscale —
            // 1080p decodifica barato em software. Fallback em cascata: hw-decode+hw-encode →
            // sw-decode+hw-encode → libx264. Cada nível apaga e recria o dir antes de tentar.
            var decodeHw = usarRkmpp && _rkmppDecodeHw && downscalePara is not null;

            var (exitCode, stderr) = await RunFfmpegHlsAsync(origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw);

            if (decodeHw && exitCode != 0)
            {
                _logger.LogWarning("rkmpp com hwaccel de decode falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando rkmpp só no encode.",
                    filmeId, exitCode, stderr);
                LimparDir(dir);
                (exitCode, stderr) = await RunFfmpegHlsAsync(origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw: false);
            }

            if (usarRkmpp)
            {
                _rkmpp.RegistrarResultado(exitCode == 0);
                if (exitCode != 0)
                {
                    _logger.LogWarning("Encode via rkmpp falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando de novo com libx264.",
                        filmeId, exitCode, stderr);
                    LimparDir(dir);
                    (exitCode, stderr) = await RunFfmpegHlsAsync(origem, dir, videoCompativel, usarRkmpp: false, audioStreamIndex, downscalePara, decodeHw: false);
                }
            }

            if (exitCode != 0)
                throw new InvalidOperationException($"ffmpeg saiu com código {exitCode}: {stderr}");

            // Remux stream-copy = cópia quase idêntica do original (sem ganho). Marca pra
            // eviction preferir despejar esse tipo primeiro quando o cache passar do teto.
            if (videoCompativel)
                try { File.WriteAllText(Path.Combine(dir, MarcadorRemux), ""); } catch (IOException) { }
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

        LimparCacheExcedente();
    }

    /// <summary>Zera o dir de cache antes de uma nova tentativa de encode. Sob o mesmo lock
    /// que protege as leituras de ObterStatusAsync — senão uma checagem concorrente pode ler
    /// o diretório no meio da troca (segments parciais já podem ter sido servidos).</summary>
    private void LimparDir(string dir)
    {
        lock (_decisaoLock)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
    }

    private async Task<(int ExitCode, string Stderr)> RunFfmpegHlsAsync(
        string origem, string dir, bool videoCompativel, bool usarRkmpp, int? audioStreamIndex, int? downscalePara, bool decodeHw)
    {
        List<string> args = ["-y"];

        // -hwaccel/-hwaccel_output_format são opções de INPUT — têm que vir ANTES do -i
        // (diferente do -init_hw_device do caminho sem hw-decode, que prepara o device pro
        // filtro/output e fica depois). decodeHw só é true no 4K+downscale via rkmpp.
        if (decodeHw)
            args.AddRange(["-init_hw_device", "rkmpp=rk", "-filter_hw_device", "rk",
                           "-hwaccel", "rkmpp", "-hwaccel_output_format", "drm_prime"]);

        // -map explícito: sem isso o ffmpeg escolhe sozinho, por heurística de codec/canais,
        // qual stream de cada tipo entra no output — e essa heurística ignora idioma e pode
        // até ignorar a flag "default" do arquivo. Em filme dual-áudio (ex.: português +
        // francês) isso já produziu HLS com a faixa errada mesmo com português marcado como
        // default. Mapear video+áudio manualmente também impede que uma legenda embutida
        // incompatível (ex.: PGS/bitmap) entre sozinha no output e quebre o encode — a API
        // não serve legenda nenhuma, então nem faz sentido incluir.
        args.AddRange(["-i", origem, "-map", "0:v:0"]);
        if (audioStreamIndex is int audioIdx) args.AddRange(["-map", $"0:{audioIdx}"]);

        if (videoCompativel)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            if (usarRkmpp && decodeHw && downscalePara is int altHw)
            {
                // Frames já vêm decodificados em memória de VPU (drm_prime). O RGA escala e
                // converte pra nv12 (8-bit) sem round-trip pela CPU; h264_rkmpp consome direto.
                args.AddRange(["-vf", $"scale_rkrga=w=-2:h={altHw}:format=nv12", "-c:v", "h264_rkmpp"]);
            }
            else if (usarRkmpp)
            {
                // Decode em software; scale/format em CPU e sobe pro VPU só pra encodar.
                // format=nv12 força 8-bit 4:2:0 (mata HDR/10-bit, mas é o que os players aqui aguentam).
                var filtro = downscalePara is int alt
                    ? $"scale=-2:{alt},format=nv12,hwupload"
                    : "format=nv12,hwupload";
                args.AddRange(["-init_hw_device", "rkmpp=rk", "-filter_hw_device", "rk", "-vf", filtro, "-c:v", "h264_rkmpp"]);
            }
            else
            {
                // -pix_fmt yuv420p: nunca deixa passar 10-bit (High 10) pro output — navegador não toca.
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p"]);
                if (downscalePara is int alt2) args.AddRange(["-vf", $"scale=-2:{alt2}"]);
            }

            // Segmentação previsível mesmo com fps/keyframes irregulares da fonte — vale
            // pros dois encoders de reencode, só não faz sentido no remux acima (-c:v copy
            // só pode cortar em keyframes que já existem no arquivo original).
            args.AddRange(["-sc_threshold", "0", "-force_key_frames", $"expr:gte(t,n_forced*{SegmentoSegundos})"]);
        }

        if (audioStreamIndex is not null) args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
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

        // Detector de travamento: se a contagem de segments não muda por _stallTimeout, o
        // ffmpeg está pendurado (já aconteceu com alguns arquivos). Mata cedo em vez de
        // segurar o slot de encode pelo _jobTimeout inteiro (6h).
        var ultimaContagem = -1;
        var ultimoProgresso = DateTime.UtcNow;
        bool Travou()
        {
            int n;
            try { n = Directory.EnumerateFiles(dir, "seg_*.ts").Count(); }
            catch { return false; }
            if (n != ultimaContagem) { ultimaContagem = n; ultimoProgresso = DateTime.UtcNow; return false; }
            return DateTime.UtcNow - ultimoProgresso > _stallTimeout;
        }

        return await ProcessRunner.ExecutarComTimeoutAsync(psi, _jobTimeout, Travou);
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

    /// <summary>Entre os streams de áudio do arquivo, escolhe o índice global (pra -map) da
    /// faixa que deve ir pro HLS: primeiro tenta achar uma em português — rip "dual áudio"
    /// costuma vir com a faixa errada marcada como default pelo grupo de release —, senão a
    /// marcada default, senão a primeira. Retorna null se o arquivo não tem áudio.</summary>
    private async Task<int?> EscolherStreamAudioAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobePath) { RedirectStandardOutput = true };
        foreach (var arg in new[]
        {
            "-v", "error", "-select_streams", "a",
            "-show_entries", "stream=index:stream_tags=language:disposition=default",
            "-of", "json", path,
        })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var saida = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        List<(int Index, string? Idioma, bool Default)> streams;
        try
        {
            using var doc = JsonDocument.Parse(saida);
            streams = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => (
                Index: s.GetProperty("index").GetInt32(),
                Idioma: s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var lang)
                    ? lang.GetString() : null,
                Default: s.GetProperty("disposition").GetProperty("default").GetInt32() == 1
            )).ToList();
        }
        catch (JsonException)
        {
            return null;
        }

        if (streams.Count == 0) return null;

        return streams.Where(s => s.Idioma is not null && IdiomasAudioPreferidos.Contains(s.Idioma))
                .Select(s => (int?)s.Index).FirstOrDefault()
            ?? streams.Where(s => s.Default).Select(s => (int?)s.Index).FirstOrDefault()
            ?? streams[0].Index;
    }

    private async Task<string?> RunFfprobeAsync(string path, string stream, CancellationToken ct)
    {
        var saida = await RodarFfprobeAsync(new[]
        {
            "-v", "error", "-select_streams", stream,
            "-show_entries", "stream=codec_name",
            "-of", "default=noprint_wrappers=1:nokey=1", path,
        }, ct);
        var valor = saida.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(valor) ? null : valor;
    }

    /// <summary>(largura, altura) do vídeo, ou null se não deu pra ler. Pede "um valor por
    /// linha" (default nokey), mas o parsing tolera qualquer separador — o ffprobe do
    /// jellyfin devolvia o CSV com uma vírgula sobrando ("3840,1920,") e quebrava tudo.</summary>
    private async Task<(int Largura, int Altura)?> ProbeResolucaoAsync(string path, CancellationToken ct)
    {
        var saida = await RodarFfprobeAsync(new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "default=noprint_wrappers=1:nokey=1", path,
        }, ct);
        var nums = saida.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return nums.Length >= 2 && int.TryParse(nums[0], out var w) && int.TryParse(nums[1], out var h) && w > 0 && h > 0
            ? (w, h)
            : null;
    }

    private async Task<string> RodarFfprobeAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobePath) { RedirectStandardOutput = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc is null) return "";
        var saida = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return saida;
    }
}
