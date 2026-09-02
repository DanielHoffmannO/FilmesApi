using System.Diagnostics;

namespace FilmesApi.Services;

/// <summary>
/// Detecta se o encoder de hardware h264_rkmpp (VPU do RK3399) funciona de fato neste
/// ambiente. O probe roda um encode sintético real (não só checa se o binário lista o
/// encoder), porque h264_rkmpp pode estar compilado no ffmpeg mas não funcionar no
/// kernel/board específico. Resultado é cacheado por todo o tempo de vida do processo.
/// </summary>
public class RkmppCapabilityService
{
    private const int MaxFalhasConsecutivas = 3;

    private readonly string _ffmpegPath;
    private readonly bool _forcarSoftware;
    private readonly ILogger<RkmppCapabilityService> _logger;
    private readonly Lazy<Task<bool>> _disponivel;

    private int _falhasConsecutivas;
    private volatile bool _desabilitadoPorFalhas;

    public RkmppCapabilityService(FfmpegOptions ffmpeg, IConfiguration config, ILogger<RkmppCapabilityService> logger)
    {
        _ffmpegPath = ffmpeg.Ffmpeg;
        _forcarSoftware = config.GetValue<bool>("ForceSoftwareEncoder");
        _logger = logger;
        _disponivel = new Lazy<Task<bool>>(ProbeAsync);
    }

    /// <summary>Se true, jobs de transcode devem tentar h264_rkmpp antes de libx264.</summary>
    public async Task<bool> DisponivelAsync()
    {
        if (_forcarSoftware || _desabilitadoPorFalhas) return false;
        return await _disponivel.Value;
    }

    /// <summary>Retrato do estado da VPU pra página de status (sem disparar o probe).</summary>
    public object Snapshot() => new
    {
        forcadoSoftware = _forcarSoftware,
        desabilitadoPorFalhas = _desabilitadoPorFalhas,
        falhasConsecutivas = _falhasConsecutivas,
        probeConcluido = _disponivel.IsValueCreated && _disponivel.Value.IsCompleted,
        disponivel = !_forcarSoftware && !_desabilitadoPorFalhas
            && _disponivel.IsValueCreated && _disponivel.Value is { IsCompletedSuccessfully: true, Result: true },
    };

    /// <summary>
    /// Chamar após cada tentativa de encode via rkmpp. Depois de falhas consecutivas
    /// demais, desliga rkmpp pelo resto do processo em vez de tentar de novo a cada filme.
    /// </summary>
    public void RegistrarResultado(bool sucesso)
    {
        if (sucesso)
        {
            Interlocked.Exchange(ref _falhasConsecutivas, 0);
            return;
        }

        if (Interlocked.Increment(ref _falhasConsecutivas) >= MaxFalhasConsecutivas)
        {
            _desabilitadoPorFalhas = true;
            _logger.LogWarning(
                "rkmpp desabilitado após {N} falhas consecutivas em runtime; usando libx264 pelo resto do processo.",
                MaxFalhasConsecutivas);
        }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath);
            psi.ArgumentList.Add("-init_hw_device");
            psi.ArgumentList.Add("rkmpp=rk");
            psi.ArgumentList.Add("-filter_hw_device");
            psi.ArgumentList.Add("rk");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("testsrc2=size=320x240:rate=25:duration=1");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add("format=nv12,hwupload");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("h264_rkmpp");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            var (exitCode, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromSeconds(10));

            if (exitCode == 0)
            {
                _logger.LogInformation("Encoder de hardware rkmpp (VPU) disponível — será preferido a libx264.");
                return true;
            }

            _logger.LogInformation("rkmpp indisponível (ffmpeg saiu com código {Code}), usando libx264. stderr: {Stderr}",
                exitCode, stderr);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Probe de rkmpp falhou ao iniciar o processo, usando libx264.");
            return false;
        }
    }
}
