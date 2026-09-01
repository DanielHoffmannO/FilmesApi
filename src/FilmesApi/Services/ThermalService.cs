using System.Globalization;

namespace FilmesApi.Services;

/// <summary>
/// Lê a temperatura da placa (<c>/sys/class/thermal/thermal_zone*/temp</c>) e segura o
/// início de um transcode enquanto estiver quente. 4K em software leva a Rock Pi a 65 °C+
/// e já obrigou reboot manual; com <c>MaxConcurrentTranscodeJobs=1</c> isso naturalmente
/// espaça os jobs e deixa a placa esfriar entre um e outro.
///
/// Desligado por padrão (<c>ThermalPauseCelsius</c> = 0). O agente do servidor liga
/// definindo o teto depois de ver os números reais no board.
/// </summary>
public class ThermalService
{
    private readonly string _raizThermal;
    private readonly double _pausarC;
    private readonly double _retomarC;
    private readonly TimeSpan _esperaMax;
    private readonly ILogger<ThermalService> _logger;

    private int _semSensorLogado;
    private volatile bool _throttling;

    public ThermalService(IConfiguration config, ILogger<ThermalService> logger)
    {
        _logger = logger;
        _raizThermal = config.GetValue<string>("ThermalRoot") ?? "/sys/class/thermal";
        _pausarC = config.GetValue<double?>("ThermalPauseCelsius") ?? 0;   // 0 = desligado
        var retomar = config.GetValue<double?>("ThermalResumeCelsius");
        _retomarC = retomar ?? Math.Max(0, _pausarC - 8);
        _esperaMax = TimeSpan.FromMinutes(config.GetValue<double?>("ThermalMaxWaitMinutes") ?? 5);
    }

    public bool Habilitado => _pausarC > 0;

    /// <summary>Maior temperatura entre as zonas térmicas, em °C. null se não deu pra ler.</summary>
    public double? TemperaturaC()
    {
        try
        {
            var arquivos = Directory.Exists(_raizThermal)
                ? Directory.EnumerateDirectories(_raizThermal, "thermal_zone*")
                    .Select(d => Path.Combine(d, "temp"))
                    .Where(File.Exists)
                : Enumerable.Empty<string>();

            double? maior = null;
            foreach (var arq in arquivos)
            {
                if (!long.TryParse(File.ReadAllText(arq).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli))
                    continue;
                var c = milli / 1000.0;
                if (c is > 0 and < 200 && (maior is null || c > maior)) maior = c;
            }

            if (maior is null && Interlocked.Exchange(ref _semSensorLogado, 1) == 0)
                _logger.LogInformation("Governador térmico ligado mas nenhuma zona térmica legível em {Raiz} — seguindo sem throttle.", _raizThermal);

            return maior;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _semSensorLogado, 1) == 0)
                _logger.LogInformation(ex, "Governador térmico: falha ao ler temperatura — seguindo sem throttle.");
            return null;
        }
    }

    public bool EstaThrottling => _throttling;

    /// <summary>Bloqueia enquanto a placa estiver acima do teto. Desiste depois de
    /// <c>ThermalMaxWaitMinutes</c> (melhor transcodificar quente do que travar o vídeo pra sempre).</summary>
    public async Task AguardarResfriamentoAsync(CancellationToken ct)
    {
        if (!Habilitado) return;

        var atual = TemperaturaC();
        if (atual is null || atual < _pausarC) return;

        _throttling = true;
        var ate = DateTime.UtcNow + _esperaMax;
        _logger.LogWarning("Placa a {Temp:F1} °C (teto {Teto} °C) — segurando o transcode até esfriar pra {Retomar} °C.",
            atual, _pausarC, _retomarC);
        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < ate)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                var t = TemperaturaC();
                if (t is null || t <= _retomarC)
                {
                    _logger.LogInformation("Placa a {Temp} °C — liberando o transcode.", t?.ToString("F1") ?? "?");
                    return;
                }
            }
            _logger.LogWarning("Placa ainda quente após {Min} min de espera — seguindo com o transcode mesmo assim.", _esperaMax.TotalMinutes);
        }
        finally
        {
            _throttling = false;
        }
    }
}
