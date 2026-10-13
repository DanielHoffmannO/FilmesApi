using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FilmesApi.Models;

namespace FilmesApi.Services;

public enum StreamStatus { Compativel, Preparando, Disponivel, Erro }
public enum RemuxStatus { Preparando, Disponivel, Erro }

/// <summary>Classificação de compatibilidade "tocar sem HLS", numa leitura de probe só —
/// <c>Compativel</c> (toca cru, sem processar nada), <c>SoAudioIncompativel</c> (vídeo ok,
/// mas precisa de /remux — áudio incompatível, 2+ faixas, OU proporção de pixel quebrada, ver
/// <see cref="CompatibilidadeVideo.SarPrecisaCorrecao"/>) ou <c>Incompativel</c> (nem isso:
/// /original é o único recurso, pode falhar dependendo do player).</summary>
public enum CompatibilidadeDireta { Compativel, SoAudioIncompativel, Incompativel }

/// <summary>
/// Garante que o vídeo seja tocável no navegador: se o codec original já é suportado,
/// serve direto; senão gera HLS incrementalmente (remux por stream-copy quando só o
/// container é o problema, ou reencode via rkmpp/libx264 quando o vídeo também precisa
/// mudar), guardando os segments em cache permanente por filme. O play começa assim que
/// o primeiro segment existe, sem esperar o filme inteiro terminar de converter.
/// </summary>
public class HlsTranscodeService
{
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
    private readonly string _remuxCachePath;
    private readonly long _remuxCacheMaxBytes;
    private readonly RkmppCapabilityService _rkmpp;
    private readonly ThermalService _thermal;
    private readonly MediaProbeService _probe;
    private readonly ILogger<HlsTranscodeService> _logger;
    /// <summary>Cancelado no shutdown do host — mata o ffmpeg em vez de deixá-lo órfão.</summary>
    private readonly CancellationToken _pararToken;

    private readonly ConcurrentDictionary<int, Task> _jobs = new();
    /// <summary>Cancela o job de HLS/remux em andamento de um filme especificamente (ex.: troca
    /// de dublagem) sem derrubar os outros — <c>_pararToken</c> sozinho só cobre shutdown do
    /// host. Dicionários separados por tipo de job (igual a <c>_jobs</c>/<c>_remuxJobs</c>):
    /// um CancellationTokenSource compartilhado entre os dois tipos arriscaria Dispose() de
    /// um lado enquanto o outro ainda está com Task.Delay(ct) pendente.</summary>
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _jobCancelHls = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _jobCancelRemux = new();
    /// <summary>Preferência manual de faixa de áudio por filme (controle remoto escolheu uma
    /// "dublagem" diferente da automática) — ver <see cref="DefinirPreferenciaAudio"/> e
    /// <see cref="EscolhaAudio.Escolher"/>. Em memória só: some num restart, volta pra automática.</summary>
    private readonly ConcurrentDictionary<int, int> _preferenciaAudio = new();
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

    /// <summary>Cache de /remux: um .mp4 completo por filme (não segmentado). Job dispara
    /// uma vez, fica em <c>_remuxJobs</c> até terminar; <c>_remuxFalhas</c> segue o mesmo
    /// cooldown de <c>_falhas</c>. Eviction por tamanho em <see cref="LimparRemuxCacheExcedente"/>.</summary>
    private readonly ConcurrentDictionary<int, Task> _remuxJobs = new();
    private readonly ConcurrentDictionary<int, DateTime> _remuxFalhas = new();
    private readonly object _remuxLock = new();

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
        _remuxCachePath = config.GetValue<string>("RemuxCachePath") ?? "/data/remux";
        var remuxMaxGb = config.GetValue<double?>("RemuxCacheMaxGB") ?? 15;
        _remuxCacheMaxBytes = remuxMaxGb > 0 ? (long)(remuxMaxGb * 1024 * 1024 * 1024) : long.MaxValue;
        _rkmpp = rkmpp;
        _thermal = thermal;
        _probe = probe;
        _logger = logger;
        Directory.CreateDirectory(_cachePath);
        Directory.CreateDirectory(_remuxCachePath);
    }

    public string DiretorioCache(int filmeId) => Path.Combine(_cachePath, filmeId.ToString());
    public string CaminhoPlaylist(int filmeId) => Path.Combine(DiretorioCache(filmeId), "playlist.m3u8");

    private static readonly Regex SegmentoValido = new(@"^seg_\d{5}\.ts$", RegexOptions.Compiled);

    /// <summary>Caminho de um segment HLS já gerado, ou null se o nome não é um segment
    /// legítimo (<c>seg_00001.ts</c> — barra path traversal) ou o arquivo não existe.
    /// Cada request de segment conta como "ainda tem gente assistindo".</summary>
    public string? ObterCaminhoSegmento(int filmeId, string arquivo)
    {
        if (!SegmentoValido.IsMatch(arquivo)) return null;
        RegistrarInteresse(filmeId);
        var caminho = Path.Combine(DiretorioCache(filmeId), arquivo);
        return File.Exists(caminho) ? caminho : null;
    }

    /// <summary>"Ainda tem gente interessada neste filme" — chamado pelo poll de status, por
    /// cada request de segmento e pelo keepalive do player. Zera o relógio de órfão.
    /// Só registra se existe job vivo ou cache pra este id: assim POST /assistindo com id
    /// aleatório (endpoint sem auth) não consegue inflar o dicionário sem limite.</summary>
    public void RegistrarInteresse(int filmeId)
    {
        if (_jobs.ContainsKey(filmeId) || _remuxJobs.ContainsKey(filmeId) || _completos.ContainsKey(filmeId)
            || Directory.Exists(DiretorioCache(filmeId)))
            _ultimoAcesso[filmeId] = DateTime.UtcNow;
    }

    /// <summary>Ninguém pede status/segmento/keepalive deste filme há mais que
    /// <paramref name="timeout"/> — quem disparou o job desistiu. Sem registro de acesso (ou
    /// timeout zero) nunca é órfão. Pura, pra testar sem subir ffmpeg.</summary>
    internal static bool InteresseExpirou(DateTime? ultimoAcesso, DateTime agora, TimeSpan timeout) =>
        timeout > TimeSpan.Zero && ultimoAcesso is DateTime visto && agora - visto > timeout;

    private bool SemInteresse(int filmeId) =>
        InteresseExpirou(_ultimoAcesso.TryGetValue(filmeId, out var visto) ? visto : null, DateTime.UtcNow, _orphanTimeout);

    /// <summary>Só responde "dá pra tocar direto?" sem disparar transcode nenhum — o
    /// <c>tv.html</c> numa TV antiga usa isso pra decidir entre /stream e /original sem HLS.</summary>
    public async Task<bool> PodeStreamDiretoAsync(string arquivoOriginal, CancellationToken ct)
        => await AnalisarCompatibilidadeAsync(arquivoOriginal, ct) == CompatibilidadeDireta.Compativel;

    /// <summary>Uma leitura de probe (cacheada) decide as três situações que <c>/pode-direto</c>
    /// precisa: toca cru, precisa só de /remux (vídeo ok, áudio não — EAC3/DTS de rip WEB-DL é
    /// o caso comum), ou nem isso (vídeo também incompatível — /original é o único recurso que
    /// sobra, às vezes falha dependendo do player).</summary>
    public async Task<CompatibilidadeDireta> AnalisarCompatibilidadeAsync(string path, CancellationToken ct)
    {
        var info = await _probe.InspecionarAsync(path, ct);
        if (info is null) return CompatibilidadeDireta.Incompativel;
        var primeiroAudio = info.Audios.Count > 0 ? info.Audios[0].Codec : null;
        return CompatibilidadeVideo.Classificar(Path.GetExtension(path), info.VideoCodec, info.Video10Bit, primeiroAudio,
            info.Audios.Count, info.SampleAspectRatio);
    }


    public string CaminhoRemux(int filmeId) => Path.Combine(_remuxCachePath, $"{filmeId}.mp4");

    /// <summary>Caminho do .mp4 remuxado se já está completo em disco (e marca o acesso pro
    /// LRU), senão null.</summary>
    public string? ObterRemuxPronto(int filmeId)
    {
        var caminho = CaminhoRemux(filmeId);
        if (!File.Exists(caminho)) return null;
        RegistrarAcessoRemux(filmeId);
        return caminho;
    }

    /// <summary>Estado do remux (cache em disco, um .mp4 por filme): dispara o job na primeira
    /// chamada, reusa nas seguintes. Ao contrário do /remux antigo (pipe ao vivo, sem
    /// Content-Length — algumas TVs recusam isso na hora, sem nem tentar), aqui o arquivo só
    /// fica "disponivel" quando está completo e gravado, servido depois com Range normal
    /// (<see cref="Controllers.ReproducaoController.ServirComRange"/>) — qualquer player aceita.
    /// Enquanto "preparando", devolve também um progresso estimado (tamanho do .tmp / tamanho
    /// do original — o vídeo é copiado quase 1:1, só o áudio encolhe, então isso sobra perto
    /// do fim; por isso o teto de 99% até realmente terminar). É só pra não parecer travado
    /// num filme de 2h+ — não precisa ser exato.</summary>
    public (RemuxStatus Status, int? ProgressoPercent) ObterStatusRemux(int filmeId, string origem)
    {
        lock (_remuxLock)
        {
            if (_remuxFalhas.TryGetValue(filmeId, out var falhouEm))
            {
                if (DateTime.UtcNow - falhouEm < _falhaCooldown) return (RemuxStatus.Erro, null);
                _remuxFalhas.TryRemove(filmeId, out _);
            }
            if (File.Exists(CaminhoRemux(filmeId))) return (RemuxStatus.Disponivel, 100);
            if (!_remuxJobs.ContainsKey(filmeId))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_pararToken);
                _jobCancelRemux[filmeId] = cts;
                _remuxJobs[filmeId] = Task.Run(() => ExecutarRemuxAsync(filmeId, origem, cts.Token));
            }
            _ultimoAcesso[filmeId] = DateTime.UtcNow;  // disparo e cada poll contam como interesse (ver ExecutarRemuxAsync)
            return (RemuxStatus.Preparando, EstimarProgressoRemux(filmeId, origem));
        }
    }

    private int? EstimarProgressoRemux(int filmeId, string origem)
    {
        try
        {
            var tmp = CaminhoRemux(filmeId) + ".tmp";
            if (!File.Exists(tmp)) return 0;
            var origBytes = new FileInfo(origem).Length;
            if (origBytes <= 0) return null;
            var pct = (int)(100.0 * new FileInfo(tmp).Length / origBytes);
            return Math.Clamp(pct, 0, 99);
        }
        catch (IOException) { return null; }
    }

    private async Task ExecutarRemuxAsync(int filmeId, string origem, CancellationToken ct)
    {
        var final = CaminhoRemux(filmeId);
        var tmp = final + ".tmp";
        var slotObtido = false;
        var orfao = false;
        try
        {
            // Mesmo gate de temperatura/concorrência do HLS: mesmo o remux sendo leve
            // (video só copia, só o áudio processa), ele ainda compete pela mesma CPU/placa —
            // sem isso um remux e um reencode HLS rodando juntos já esquentaram a placa hoje.
            await _thermal.AguardarResfriamentoAsync(ct);
            await _slotEncoder.WaitAsync(ct);
            slotObtido = true;
            await _thermal.AguardarResfriamentoAsync(ct);

            // Ficou na fila (slot/temperatura) e quem pediu já foi embora: nem começa.
            if (SemInteresse(filmeId)) { RemuxOrfao(filmeId); return; }

            var info = await _probe.InspecionarAsync(origem, CancellationToken.None)
                ?? throw new InvalidOperationException("probe falhou");
            var preferencia = _preferenciaAudio.TryGetValue(filmeId, out var pref) ? pref : (int?)null;
            var audioIdx = EscolhaAudio.Escolher(info.Audios, preferencia);
            var audioJaCompativel = EscolhaAudio.JaCompativel(info.Audios.FirstOrDefault(a => a.Index == audioIdx));

            var args = FfmpegArgs.Remux(origem, tmp, audioIdx, info.Largura, info.Altura, audioJaCompativel);

            var psi = new ProcessStartInfo(_ffmpegPath);
            foreach (var a in args) psi.ArgumentList.Add(a);

            // Mesmo detector de órfão do HLS: pré-busca do próximo episódio que ninguém chegou
            // a assistir não pode remuxar o arquivo inteiro de graça.
            bool Travou()
            {
                if (!SemInteresse(filmeId)) return false;
                orfao = true;
                return true;
            }

            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, _jobTimeout, Travou, ct);
            if (orfao)
            {
                RemuxOrfao(filmeId);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return;
            }
            if (exit != 0) throw new InvalidOperationException($"ffmpeg exit {exit}: {stderr}");

            File.Move(tmp, final, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            // Shutdown do host OU troca manual de dublagem (DefinirPreferenciaAudio) — não
            // marca _remuxFalhas: fica retentável na próxima vez que alguém pedir /remux-status.
            _logger.LogInformation("Remux do filme {Id} interrompido.", filmeId);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remuxar filme {Id}.", filmeId);
            _remuxFalhas[filmeId] = DateTime.UtcNow;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally
        {
            _remuxJobs.TryRemove(filmeId, out _);
            if (_jobCancelRemux.TryRemove(filmeId, out var cts)) cts.Dispose();
            if (slotObtido) _slotEncoder.Release();
        }

        if (!ct.IsCancellationRequested) LimparRemuxCacheExcedente();
    }

    /// <summary>Remux abandonado: não marca _remuxFalhas (fica retentável quando alguém pedir de novo).</summary>
    private void RemuxOrfao(int filmeId)
    {
        _logger.LogInformation("Remux do filme {Id} abortado: ninguém pediu status há mais de {Seg}s.",
            filmeId, _orphanTimeout.TotalSeconds);
        _ultimoAcesso.TryRemove(filmeId, out _);
    }

    /// <summary>Toca o mtime do .mp4 remuxado — sinal de LRU pra eviction, mesmo padrão do
    /// <see cref="RegistrarAcesso"/> do HLS. Chamado a cada GET de /remux (que não passa mais
    /// por <see cref="ObterStatusRemux"/> depois de "disponivel" — só assim o arquivo não
    /// parece "sem uso" pra sempre depois da primeira vez).</summary>
    private void RegistrarAcessoRemux(int filmeId)
    {
        try
        {
            var caminho = CaminhoRemux(filmeId);
            if (!File.Exists(caminho)) return;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(caminho) > TimeSpan.FromMinutes(2))
                File.SetLastWriteTimeUtc(caminho, DateTime.UtcNow);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Se o cache de /remux passou do teto (<c>RemuxCacheMaxGB</c>), despeja os .mp4
    /// menos recentemente acessados até voltar pra ~90%. Sem isso, todo filme com áudio
    /// incompatível que alguém já abriu fica em cache pra sempre (cada um é o filme inteiro,
    /// ~mesmo tamanho do original).</summary>
    private void LimparRemuxCacheExcedente()
    {
        if (_remuxCacheMaxBytes == long.MaxValue) return;
        try
        {
            if (!Directory.Exists(_remuxCachePath)) return;

            var arquivos = Directory.EnumerateFiles(_remuxCachePath, "*.mp4")
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            var total = arquivos.Sum(f => f.Length);
            if (total <= _remuxCacheMaxBytes) return;

            var alvo = (long)(_remuxCacheMaxBytes * 0.9);
            var corte = DateTime.UtcNow - TimeSpan.FromMinutes(30);
            foreach (var f in arquivos)
            {
                if (total <= alvo) break;
                if (f.LastWriteTimeUtc >= corte) continue;  // acessado há pouco, pode estar em uso
                try { f.Delete(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                total -= f.Length;
                _logger.LogInformation("Cache de remux acima do teto: despejando {Nome} ({Mb} MB).", f.Name, f.Length / 1024 / 1024);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao avaliar/despejar cache de remux excedente.");
        }
    }

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

    /// <summary>Faixas de áudio do arquivo ("dublagens" disponíveis), com qual delas o
    /// remux/HLS usaria agora — pro controle.html listar e já destacar a atual. Ver
    /// <see cref="EscolhaAudio.Escolher"/>.</summary>
    public async Task<IReadOnlyList<AudioInfo>> ListarAudiosAsync(int filmeId, string arquivoAbsoluto, CancellationToken ct)
    {
        var info = await _probe.InspecionarAsync(arquivoAbsoluto, ct);
        if (info is null || info.Audios.Count == 0) return [];

        var preferencia = _preferenciaAudio.TryGetValue(filmeId, out var pref) ? pref : (int?)null;
        var escolhida = EscolhaAudio.Escolher(info.Audios, preferencia);
        return info.Audios
            .Select(a => new AudioInfo(a.Index, a.Codec, a.Idioma, a.Default, a.Index == escolhida))
            .ToList();
    }

    /// <summary>Troca a faixa de áudio preferida manualmente pra este filme (controle remoto —
    /// "dublagem" em <c>controle.html</c>), sobrepondo a escolha automática por idioma de
    /// <see cref="EscolhaAudio.Escolher"/>. Cancela um job de HLS/remux em andamento pra este
    /// filme (se houver) e apaga o cache já pronto (HLS e /remux), pra forçar regerar com a
    /// faixa nova na próxima vez que /hls/playlist.m3u8 ou /remux-status for pedido — o áudio
    /// vai dentro dos segments/arquivo, não dá pra trocar sem reprocessar.</summary>
    public void DefinirPreferenciaAudio(int filmeId, int audioStreamIndex)
    {
        _preferenciaAudio[filmeId] = audioStreamIndex;

        if (_jobCancelHls.TryGetValue(filmeId, out var ctsHls))
            try { ctsHls.Cancel(); } catch (ObjectDisposedException) { }
        if (_jobCancelRemux.TryGetValue(filmeId, out var ctsRemux))
            try { ctsRemux.Cancel(); } catch (ObjectDisposedException) { }

        LimparCache(filmeId);
        try
        {
            var caminhoRemux = CaminhoRemux(filmeId);
            if (File.Exists(caminhoRemux)) File.Delete(caminhoRemux);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _remuxFalhas.TryRemove(filmeId, out _);
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
        if (await AnalisarCompatibilidadeAsync(arquivoOriginal, ct) == CompatibilidadeDireta.Compativel)
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
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_pararToken);
            _jobCancelHls[filmeId] = cts;
            _jobs[filmeId] = Task.Run(() => TranscodificarHlsAsync(filmeId, arquivoOriginal, cts.Token), CancellationToken.None);
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

    private async Task TranscodificarHlsAsync(int filmeId, string origem, CancellationToken ct)
    {
        var dir = DiretorioCache(filmeId);

        // Espera a placa esfriar ANTES de tomar o slot: com MaxConcurrentTranscodeJobs > 1,
        // segurar só depois do slot deixaria os dois jobs entrarem e rodarem quentes juntos —
        // o gate só pegaria o terceiro. Aqui, um job novo não ocupa slot enquanto a placa
        // está no teto.
        var slotObtido = false;
        try
        {
            await _thermal.AguardarResfriamentoAsync(ct);
            await _slotEncoder.WaitAsync(ct);
            slotObtido = true;

            // Rechecagem: o slot pode ter demorado a liberar e a placa esquentado de novo
            // (o job anterior encerrou quente). Barato quando já está fria.
            await _thermal.AguardarResfriamentoAsync(ct);

            // Uma leitura de ffprobe só (cacheada): codec, resolução e faixa de áudio.
            var info = await _probe.InspecionarAsync(origem, CancellationToken.None)
                       ?? throw new InvalidOperationException("ffprobe não conseguiu ler o arquivo.");
            var videoCompativel = CompatibilidadeVideo.VideoTocavelSemReencode(info.VideoCodec, info.Video10Bit);

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
            var preferencia = _preferenciaAudio.TryGetValue(filmeId, out var pref) ? pref : (int?)null;
            var audioStreamIndex = EscolhaAudio.Escolher(info.Audios, preferencia);
            var audioJaCompativel = EscolhaAudio.JaCompativel(info.Audios.FirstOrDefault(a => a.Index == audioStreamIndex));

            // Decode por hardware só faz diferença (e só vale o risco) no 4K com downscale —
            // 1080p decodifica barato em software. Fallback em cascata: hw-decode+hw-encode →
            // sw-decode+hw-encode → libx264. Cada nível apaga e recria o dir antes de tentar.
            var decodeHw = usarRkmpp && _rkmppDecodeHw && downscalePara is not null;

            var (exitCode, stderr, orfao, travouSemProgresso) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw, info.Largura, info.Altura, audioJaCompativel, ct);

            if (!orfao && decodeHw && exitCode != 0)
            {
                _logger.LogWarning("rkmpp com hwaccel de decode falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando rkmpp só no encode.",
                    filmeId, exitCode, stderr);
                LimparDir(dir);
                (exitCode, stderr, orfao, travouSemProgresso) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw: false, info.Largura, info.Altura, audioJaCompativel, ct);
            }

            if (ContaComoFalhaDeEncoder(orfao, travouSemProgresso) && usarRkmpp)
            {
                _rkmpp.RegistrarResultado(exitCode == 0);
                if (exitCode != 0)
                {
                    _logger.LogWarning("Encode via rkmpp falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando de novo com libx264.",
                        filmeId, exitCode, stderr);
                    LimparDir(dir);
                    (exitCode, stderr, orfao, travouSemProgresso) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp: false, audioStreamIndex, downscalePara, decodeHw: false, info.Largura, info.Altura, audioJaCompativel, ct);
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
            // Shutdown do host, placa quente demais por tempo demais, OU troca manual de
            // dublagem (DefinirPreferenciaAudio cancela o job em andamento). Não marca
            // _falhas: fica retentável no próximo boot / próxima abertura / próxima tentativa.
            _logger.LogInformation("Transcode do filme {Id} interrompido.", filmeId);
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
            if (_jobCancelHls.TryRemove(filmeId, out var cts)) cts.Dispose();
            if (slotObtido) _slotEncoder.Release();
        }

        if (!ct.IsCancellationRequested) LimparCacheExcedente();
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

    private async Task<(int ExitCode, string Stderr, bool Orfao, bool TravouSemProgresso)> RunFfmpegHlsAsync(
        int filmeId, string origem, string dir, bool videoCompativel, bool usarRkmpp, int? audioStreamIndex, int? downscalePara, bool decodeHw,
        int largura, int altura, bool audioJaCompativel, CancellationToken ct)
    {
        var args = FfmpegArgs.Hls(origem, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw, largura, altura, audioJaCompativel);

        var psi = new ProcessStartInfo(_ffmpegPath) { WorkingDirectory = dir };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // Detector de travamento: se a contagem de segments não muda por _stallTimeout, o
        // ffmpeg está pendurado (já aconteceu com alguns arquivos). Mata cedo em vez de
        // segurar o slot de encode pelo _jobTimeout inteiro (6h).
        var ultimaContagem = -1;
        var ultimoProgresso = DateTime.UtcNow;
        var orfao = false;
        var travouSemProgresso = false;
        bool Travou()
        {
            // Ninguém pede status/segmento deste filme há muito tempo — quem pediu desistiu.
            // Aborta pra não fritar a placa 1h transcodificando um 4K que ninguém vai ver.
            if (SemInteresse(filmeId))
            {
                orfao = true;
                return true;
            }

            int n;
            try { n = Directory.EnumerateFiles(dir, "seg_*.ts").Count(); }
            catch { return false; }
            if (n != ultimaContagem) { ultimaContagem = n; ultimoProgresso = DateTime.UtcNow; return false; }
            if (DateTime.UtcNow - ultimoProgresso <= _stallTimeout) return false;

            // Travou de verdade (sem progresso) — diferente do órfão acima. Não é falha do
            // encoder (disco lento, placa quente, arquivo pesado): ver ContaComoFalhaDeEncoder.
            travouSemProgresso = true;
            return true;
        }

        var (exitCode, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, _jobTimeout, Travou, ct);
        return (exitCode, stderr, orfao, travouSemProgresso);
    }

    /// <summary>Um job morto pelo detector de travamento (sem progresso por <c>_stallTimeout</c>)
    /// não é uma falha do encoder de hardware — pode ser disco/mount lento, placa quente ou só
    /// um arquivo pesado. Só um <c>exitCode</c> genuinamente diferente de zero deveria contar
    /// contra o rkmpp (o caso de órfão já era filtrado à parte). Extraído e testado à parte
    /// porque é fácil "simplificar" essa condição de volta pra só <c>!orfao</c> sem perceber que
    /// isso desliga a VPU pelo resto do processo depois de 3 travamentos que nada têm a ver com
    /// o hardware — degradação progressiva que só um restart do container conserta.</summary>
    internal static bool ContaComoFalhaDeEncoder(bool orfao, bool travouSemProgresso) => !orfao && !travouSemProgresso;


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
