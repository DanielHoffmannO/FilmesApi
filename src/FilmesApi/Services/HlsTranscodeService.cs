using System.Collections.Concurrent;
using System.Text.RegularExpressions;

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
public partial class HlsTranscodeService
{
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
    private readonly PreferenciasAudioService _prefs;

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
    private (long HlsBytes, int HlsItens, long RemuxBytes, int RemuxItens, DateTime Quando) _cacheStats = (0, 0, 0, 0, DateTime.MinValue);
    private readonly EscritaDeCache _escrita = new();

    /// <summary>Quantos jobs (HLS ou remux) estão parados esperando um slot de encoder — o pré-aquecimento só começa com
    /// zero e cede o lugar se aparecer alguém (ver <see cref="PoliticaPreAquecimento"/>).</summary>
    private int _esperandoSlot;
    /// <summary>Remuxes iniciados só pra aquecer (ninguém pediu ainda): não dependem de alguém olhando e cedem o lugar.
    /// Saem daqui quando um pedido de verdade chega (<see cref="ObterStatusRemux"/>) ou o job termina.</summary>
    private readonly ConcurrentDictionary<int, byte> _remuxQuente = new();

    public HlsTranscodeService(FfmpegOptions ffmpeg, IConfiguration config, RkmppCapabilityService rkmpp,
        ThermalService thermal, MediaProbeService probe, ILogger<HlsTranscodeService> logger,
        IHostApplicationLifetime lifetime, PreferenciasAudioService prefs)
    {
        _prefs = prefs;
        _pararToken = lifetime.ApplicationStopping;
        _cachePath = CachesDeDisco.Hls(config);
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
        _remuxCachePath = CachesDeDisco.Remux(config);
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
    public string CaminhoPlaylist(int filmeId) => Path.Combine(DiretorioCache(filmeId), NomesDeCache.Playlist);

    private static readonly Regex SegmentoValido = new(@"^seg_[0-9]{5}\.ts\z", RegexOptions.Compiled);

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
}
