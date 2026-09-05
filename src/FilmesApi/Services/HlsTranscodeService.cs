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
    private static readonly string[] IdiomasAudioPreferidos = ["por", "pt", "pob"];

    /// <summary>Nome do marcador que sinaliza "esse cache é só remux stream-copy" — pura
    /// duplicata do original, sem ganho de compressão, então é o primeiro a ser despejado.</summary>
    private const string MarcadorRemux = ".remux";

    private readonly string _cachePath;
    private readonly string _ffmpegPath;
    private readonly TimeSpan _jobTimeout;
    private readonly TimeSpan _stallTimeout;
    private readonly long _cacheMaxBytes;
    private readonly int _alturaMaxReencode;
    private readonly bool _rkmppDecodeHw;
    private readonly TimeSpan _orphanTimeout;
    private readonly TimeSpan _falhaCooldown;
    private readonly int _maxJobs;
    private readonly SemaphoreSlim _slotEncoder;
    private readonly RkmppCapabilityService _rkmpp;
    private readonly ThermalService _thermal;
    private readonly MediaProbeService _probe;
    private readonly ILogger<HlsTranscodeService> _logger;
    /// <summary>Cancelado no shutdown do host — mata o ffmpeg em vez de deixá-lo órfão.</summary>
    private readonly CancellationToken _pararToken;

    private readonly ConcurrentDictionary<int, Task> _jobs = new();
    /// <summary>filmeId -> quando o transcode falhou. Não é permanente: depois de
    /// <c>HlsFalhaCooldownMinutes</c> a entrada expira e o filme volta a ser tentável
    /// (falha de ffmpeg costuma ser transitória — arquivo meio corrompido, placa quente).</summary>
    private readonly ConcurrentDictionary<int, DateTime> _falhas = new();
    private readonly ConcurrentDictionary<int, bool> _completos = new();
    /// <summary>Último instante em que alguém pediu status/segmento/keepalive deste filme.
    /// Se ninguém pede há <c>HlsOrphanTimeoutSeconds</c>, o transcode em andamento é abortado
    /// (o caso clássico: abriu um 4K, viu "Preparando", desistiu — e o ffmpeg segue 1h à toa).</summary>
    private readonly ConcurrentDictionary<int, DateTime> _ultimoAcesso = new();
    private readonly object _decisaoLock = new();

    /// <summary>(bytes, itens, quando) do cache HLS em disco — varrido no máx. a cada 20 s
    /// (a página de status é refresh manual, não vale o I/O). Ver <see cref="ObterSnapshot"/>.</summary>
    private (long Bytes, int Itens, DateTime Quando) _cacheStats = (0, 0, DateTime.MinValue);

    public HlsTranscodeService(FfmpegOptions ffmpeg, IConfiguration config, RkmppCapabilityService rkmpp,
        ThermalService thermal, MediaProbeService probe, ILogger<HlsTranscodeService> logger,
        IHostApplicationLifetime lifetime)
    {
        _pararToken = lifetime.ApplicationStopping;
        _cachePath = config.GetValue<string>("HlsCachePath") ?? "/data/hls";
        _ffmpegPath = ffmpeg.Ffmpeg;
        _maxJobs = config.GetValue<int?>("MaxConcurrentTranscodeJobs") ?? 1;
        _jobTimeout = TimeSpan.FromHours(config.GetValue<double?>("TranscodeJobTimeoutHours") ?? 6);
        _stallTimeout = TimeSpan.FromMinutes(config.GetValue<double?>("HlsStallTimeoutMinutes") ?? 8);
        var maxGb = config.GetValue<double?>("HlsCacheMaxGB") ?? 20;
        _cacheMaxBytes = maxGb > 0 ? (long)(maxGb * 1024 * 1024 * 1024) : long.MaxValue;
        _alturaMaxReencode = config.GetValue<int?>("HlsMaxAlturaReencode") ?? 1080;  // 0 = sem downscale
        // Decode via VPU no caminho rkmpp (só pros 4K com downscale). Off por padrão:
        // depende de scale_rkrga estar no ffmpeg e de o device aceitar drm_prime — o
        // agente do servidor liga isso só depois de validar por linha de comando.
        _rkmppDecodeHw = config.GetValue<bool?>("HlsRkmppDecodeHw") ?? false;
        _orphanTimeout = TimeSpan.FromSeconds(config.GetValue<double?>("HlsOrphanTimeoutSeconds") ?? 90);  // 0 = nunca aborta
        _falhaCooldown = TimeSpan.FromMinutes(config.GetValue<double?>("HlsFalhaCooldownMinutes") ?? 10);
        _slotEncoder = new SemaphoreSlim(_maxJobs, _maxJobs);
        _rkmpp = rkmpp;
        _thermal = thermal;
        _probe = probe;
        _logger = logger;
        Directory.CreateDirectory(_cachePath);
    }

    public string DiretorioCache(int filmeId) => Path.Combine(_cachePath, filmeId.ToString());
    public string CaminhoPlaylist(int filmeId) => Path.Combine(DiretorioCache(filmeId), "playlist.m3u8");

    /// <summary>"Ainda tem gente interessada neste filme" — chamado pelo poll de status, por
    /// cada request de segmento e pelo keepalive do player. Zera o relógio de órfão.
    /// Só registra se existe job vivo ou cache pra este id: assim POST /assistindo com id
    /// aleatório (endpoint sem auth) não consegue inflar o dicionário sem limite.</summary>
    public void RegistrarInteresse(int filmeId)
    {
        if (_jobs.ContainsKey(filmeId) || _completos.ContainsKey(filmeId) || Directory.Exists(DiretorioCache(filmeId)))
            _ultimoAcesso[filmeId] = DateTime.UtcNow;
    }

    /// <summary>Só responde "dá pra tocar direto?" sem disparar transcode nenhum — a
    /// <c>feia.html</c> usa isso pra decidir entre /stream e /original sem esperar HLS.</summary>
    public Task<bool> PodeStreamDiretoAsync(string arquivoOriginal, CancellationToken ct)
        => EhCompativelAsync(arquivoOriginal, ct);

    /// <summary>Tem algum job de transcode vivo (encodando ou na fila)? Barato — sem I/O.</summary>
    public bool TemJobAtivo() => !_jobs.IsEmpty;

    /// <summary>Este filme tem job de transcode vivo? Barato — sem I/O.</summary>
    public bool TemJobDoFilme(int filmeId) => _jobs.ContainsKey(filmeId);

    /// <summary>Apaga best-effort o cache HLS do filme — usado ao deletar o filme do catálogo.</summary>
    public void LimparCache(int filmeId)
    {
        _ultimoAcesso.TryRemove(filmeId, out _);
        _falhas.TryRemove(filmeId, out _);
        _completos.TryRemove(filmeId, out _);
        try
        {
            lock (_decisaoLock)
            {
                var dir = DiretorioCache(filmeId);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
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
                // "Acessado" = mtime do playlist OU keepalive recente (_ultimoAcesso). O mtime
                // para de subir quando o playback vira só fetch de segmento, então sozinho ele
                // acha que ninguém está assistindo um filme que está tocando.
                if (_ultimoAcesso.TryGetValue(id, out var ka) && ka > acesso) acesso = ka;
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

        RegistrarInteresse(filmeId);
        var dir = DiretorioCache(filmeId);
        var playlist = CaminhoPlaylist(filmeId);

        // Toda a decisão (falha permanente / cache completo / job em andamento / começar do
        // zero) precisa ser uma única seção síncrona e atômica sob o mesmo lock que protege
        // as mutações do job (TranscodificarHlsAsync) — senão duas requisições concorrentes
        // podem, por exemplo, ler "sem job, sem ENDLIST" bem no instante em que o job estava
        // terminando com sucesso, e apagar um cache recém-completo pra recomeçar à toa.
        lock (_decisaoLock)
        {
            if (_falhas.TryGetValue(filmeId, out var falhouEm))
            {
                if (DateTime.UtcNow - falhouEm < _falhaCooldown)
                    return (StreamStatus.Erro, null);
                _falhas.TryRemove(filmeId, out _);  // cooldown passou — deixa tentar de novo
            }

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
            _ultimoAcesso[filmeId] = DateTime.UtcNow;  // já conta como interesse — o job acabou de nascer
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

        // Espera a placa esfriar ANTES de tomar o slot: com MaxConcurrentTranscodeJobs > 1,
        // segurar só depois do slot deixaria os dois jobs entrarem e rodarem quentes juntos —
        // o gate só pegaria o terceiro. Aqui, um job novo não ocupa slot enquanto a placa
        // está no teto.
        var slotObtido = false;
        try
        {
            await _thermal.AguardarResfriamentoAsync(_pararToken);
            await _slotEncoder.WaitAsync(_pararToken);
            slotObtido = true;

            // Rechecagem: o slot pode ter demorado a liberar e a placa esquentado de novo
            // (o job anterior encerrou quente). Barato quando já está fria.
            await _thermal.AguardarResfriamentoAsync(_pararToken);

            // Uma leitura de ffprobe só (cacheada): codec, resolução e faixa de áudio.
            var info = await _probe.InspecionarAsync(origem, CancellationToken.None)
                       ?? throw new InvalidOperationException("ffprobe não conseguiu ler o arquivo.");
            var videoCodec = info.VideoCodec;
            var videoCompativel = videoCodec is not null && VideoCodecsCompativeis.Contains(videoCodec);

            // 4K/UHD decodificado + encodado em software trava o Radxa por minutos e esquenta
            // a placa. Se vamos reencodar e a entrada passa do teto, reduz a resolução no
            // filtro do ffmpeg — corta o custo de encode drasticamente. Perde resolução,
            // ganha estabilidade.
            int? downscalePara = null;
            if (!videoCompativel && _alturaMaxReencode > 0 && info.Altura > _alturaMaxReencode)
            {
                downscalePara = _alturaMaxReencode;
                _logger.LogWarning(
                    "Filme {Id}: entrada {W}x{H} — reencode com downscale pra {Alvo}p (4K+ em software sobrecarrega o servidor).",
                    filmeId, info.Largura, info.Altura, _alturaMaxReencode);
            }

            var usarRkmpp = !videoCompativel && await _rkmpp.DisponivelAsync();
            var audioStreamIndex = EscolherStreamAudio(info.Audios);

            // Decode por hardware só faz diferença (e só vale o risco) no 4K com downscale —
            // 1080p decodifica barato em software. Fallback em cascata: hw-decode+hw-encode →
            // sw-decode+hw-encode → libx264. Cada nível apaga e recria o dir antes de tentar.
            var decodeHw = usarRkmpp && _rkmppDecodeHw && downscalePara is not null;

            var (exitCode, stderr, orfao) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw);

            if (!orfao && decodeHw && exitCode != 0)
            {
                _logger.LogWarning("rkmpp com hwaccel de decode falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando rkmpp só no encode.",
                    filmeId, exitCode, stderr);
                LimparDir(dir);
                (exitCode, stderr, orfao) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw: false);
            }

            if (!orfao && usarRkmpp)
            {
                _rkmpp.RegistrarResultado(exitCode == 0);
                if (exitCode != 0)
                {
                    _logger.LogWarning("Encode via rkmpp falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando de novo com libx264.",
                        filmeId, exitCode, stderr);
                    LimparDir(dir);
                    (exitCode, stderr, orfao) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp: false, audioStreamIndex, downscalePara, decodeHw: false);
                }
            }

            if (orfao)
            {
                // Ninguém mais assistindo — aborta sem marcar _falhas, pra ficar retentável
                // quando/se abrirem de novo. LimparCacheExcedente() no fim é pulado de propósito.
                _logger.LogInformation("Transcode do filme {Id} abortado: ninguém pediu status/segmento há mais de {Seg}s.",
                    filmeId, _orphanTimeout.TotalSeconds);
                LimparDir(dir);
                _ultimoAcesso.TryRemove(filmeId, out _);
                return;
            }

            if (exitCode != 0)
                throw new InvalidOperationException($"ffmpeg saiu com código {exitCode}: {stderr}");

            // Remux stream-copy = cópia quase idêntica do original (sem ganho). Marca pra
            // eviction preferir despejar esse tipo primeiro quando o cache passar do teto.
            if (videoCompativel)
                try { File.WriteAllText(Path.Combine(dir, MarcadorRemux), ""); } catch (IOException) { }
        }
        catch (OperationCanceledException)
        {
            // Shutdown do host (ou placa quente demais por tempo demais). Não marca _falhas:
            // fica retentável no próximo boot / próxima abertura.
            _logger.LogInformation("Transcode do filme {Id} interrompido (host encerrando).", filmeId);
            try { lock (_decisaoLock) { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao transcodificar filme {Id} para HLS.", filmeId);
            lock (_decisaoLock)
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                _falhas[filmeId] = DateTime.UtcNow;
            }
        }
        finally
        {
            lock (_decisaoLock) { _jobs.TryRemove(filmeId, out _); }
            if (slotObtido) _slotEncoder.Release();
        }

        if (!_pararToken.IsCancellationRequested) LimparCacheExcedente();
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

    /// <summary>Monta a linha de argumentos do ffmpeg pro job HLS. Pura e <c>internal</c>
    /// de propósito: dá pra testar a decisão de encode (ex.: o <c>-ac 2</c> obrigatório)
    /// sem subir processo — ver <c>FilmesApi.Tests</c>.</summary>
    internal static List<string> MontarArgsFfmpegHls(
        string origem, bool videoCompativel, bool usarRkmpp, int? audioStreamIndex, int? downscalePara, bool decodeHw)
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

        // -ac 2: downmix pra estéreo SEMPRE. Áudio 5.1/7.1 (EAC3 de WEB-DL é o caso comum)
        // reencodado pra AAC multicanal sai sem channel_layout que o decoder AAC do Chrome
        // (MSE/hls.js) reconheça — e ele rejeita EM SILÊNCIO: a API responde 200, playlist e
        // segments existem, mas o vídeo não toca e não há erro em log nenhum. ffprobe e players
        // desktop toleram, então é quase impossível diagnosticar sem saber disso de antemão.
        if (audioStreamIndex is not null) args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ac", "2"]);
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
        return args;
    }

    private async Task<(int ExitCode, string Stderr, bool Orfao)> RunFfmpegHlsAsync(
        int filmeId, string origem, string dir, bool videoCompativel, bool usarRkmpp, int? audioStreamIndex, int? downscalePara, bool decodeHw)
    {
        var args = MontarArgsFfmpegHls(origem, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw);

        var psi = new ProcessStartInfo(_ffmpegPath) { WorkingDirectory = dir };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // Detector de travamento: se a contagem de segments não muda por _stallTimeout, o
        // ffmpeg está pendurado (já aconteceu com alguns arquivos). Mata cedo em vez de
        // segurar o slot de encode pelo _jobTimeout inteiro (6h).
        var ultimaContagem = -1;
        var ultimoProgresso = DateTime.UtcNow;
        var orfao = false;
        bool Travou()
        {
            // Ninguém pede status/segmento deste filme há muito tempo — quem pediu desistiu.
            // Aborta pra não fritar a placa 1h transcodificando um 4K que ninguém vai ver.
            if (_orphanTimeout > TimeSpan.Zero
                && _ultimoAcesso.TryGetValue(filmeId, out var visto)
                && DateTime.UtcNow - visto > _orphanTimeout)
            {
                orfao = true;
                return true;
            }

            int n;
            try { n = Directory.EnumerateFiles(dir, "seg_*.ts").Count(); }
            catch { return false; }
            if (n != ultimaContagem) { ultimaContagem = n; ultimoProgresso = DateTime.UtcNow; return false; }
            return DateTime.UtcNow - ultimoProgresso > _stallTimeout;
        }

        var (exitCode, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, _jobTimeout, Travou, _pararToken);
        return (exitCode, stderr, orfao);
    }

    private async Task<bool> EhCompativelAsync(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mp4" or ".webm" or ".mov" or ".m4v")) return false;

        var info = await _probe.InspecionarAsync(path, ct);
        return info is not null && PodeTocarDireto(info);
    }

    private static bool PodeTocarDireto(MediaInfo info)
    {
        var videoOk = info.VideoCodec is not null && VideoCodecsCompativeis.Contains(info.VideoCodec);
        var primeiroAudio = info.Audios.Count > 0 ? info.Audios[0].Codec : null;
        var audioOk = primeiroAudio is null || AudioCodecsCompativeis.Contains(primeiroAudio);
        return videoOk && audioOk;
    }

    /// <summary>Índice global (pra <c>-map</c>) da faixa de áudio que vai pro HLS: primeiro
    /// uma em português — rip "dual áudio" costuma marcar a faixa errada como default —, senão
    /// a default, senão a primeira. null se não há áudio. Função pura sobre o probe.</summary>
    private static int? EscolherStreamAudio(IReadOnlyList<FaixaAudio> audios)
    {
        if (audios.Count == 0) return null;
        return audios.Where(a => a.Idioma is not null && IdiomasAudioPreferidos.Contains(a.Idioma))
                .Select(a => (int?)a.Index).FirstOrDefault()
            ?? audios.Where(a => a.Default).Select(a => (int?)a.Index).FirstOrDefault()
            ?? audios[0].Index;
    }

    private readonly object _cacheStatsLock = new();

    /// <summary>Retrato do estado do transcode pra página de status (que faz poll a cada ~3 s).
    /// A varredura de tamanho do cache em disco é feita no máximo a cada 20 s.</summary>
    public Models.HlsStatusSnapshot ObterSnapshot()
    {
        var jobs = _jobs.Keys.OrderBy(x => x).ToArray();
        var encodando = Math.Max(0, _maxJobs - _slotEncoder.CurrentCount);

        (long Bytes, int Itens, DateTime _) stats;
        lock (_cacheStatsLock)
        {
            if (DateTime.UtcNow - _cacheStats.Quando > TimeSpan.FromSeconds(20))
            {
                long total = 0;
                var itens = 0;
                try
                {
                    if (Directory.Exists(_cachePath))
                        foreach (var dir in Directory.EnumerateDirectories(_cachePath))
                        {
                            itens++;
                            foreach (var arq in Directory.EnumerateFiles(dir))
                                try { total += new FileInfo(arq).Length; } catch (IOException) { }
                        }
                }
                catch (IOException) { }
                _cacheStats = (total, itens, DateTime.UtcNow);
            }
            stats = _cacheStats;
        }

        return new Models.HlsStatusSnapshot(
            JobsAtivos: jobs.Length,
            Encodando: encodando,
            NaFila: Math.Max(0, jobs.Length - encodando),
            Completos: _completos.Count,
            Falhas: _falhas.Count,
            FilmesEmJob: jobs,
            FilmesComFalha: _falhas.Keys.OrderBy(x => x).ToArray(),
            CacheBytes: stats.Bytes,
            CacheMaxBytes: _cacheMaxBytes == long.MaxValue ? 0 : _cacheMaxBytes,
            CacheItens: stats.Itens);
    }
}
