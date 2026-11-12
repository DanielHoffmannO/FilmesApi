namespace FilmesApi.Services;

/// <summary>
/// Junta uma rajada de pedidos de scan num só. O *arr manda um webhook por episódio importado (um season pack são dezenas
/// em poucos segundos): cada pedido adia o scan em <c>espera</c>, e um teto (<c>esperaMaxima</c>, contado do 1º pedido)
/// garante que uma rajada comprida não adia pra sempre. Lógica pura: o "agora" entra por parâmetro como um instante
/// MONOTÔNICO (<see cref="TimeSpan"/> de um <see cref="System.Diagnostics.Stopwatch"/>) — a placa não tem RTC e o relógio de
/// parede pula quando o NTP acerta.
/// </summary>
public sealed class DebounceDeScan
{
    public static readonly TimeSpan EsperaPadrao = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan EsperaMaximaPadrao = TimeSpan.FromSeconds(60);

    private readonly object _trava = new();
    private readonly TimeSpan _espera;
    private readonly TimeSpan _maxima;
    private TimeSpan? _primeiro;
    private TimeSpan _ultimo;

    public DebounceDeScan(TimeSpan espera, TimeSpan esperaMaxima)
    {
        _espera = espera < TimeSpan.Zero ? TimeSpan.Zero : espera;
        _maxima = esperaMaxima < _espera ? _espera : esperaMaxima;   // o teto nunca é menor que a espera
    }

    /// <summary>Lê <c>Webhook:DebounceSegundos</c> (12) e <c>Webhook:EsperaMaximaSegundos</c> (60).</summary>
    public static DebounceDeScan De(IConfiguration config) => new(
        TimeSpan.FromSeconds(config.GetValue<double?>("Webhook:DebounceSegundos") ?? EsperaPadrao.TotalSeconds),
        TimeSpan.FromSeconds(config.GetValue<double?>("Webhook:EsperaMaximaSegundos") ?? EsperaMaximaPadrao.TotalSeconds));

    /// <summary>Há pedido esperando?</summary>
    public bool Pendente { get { lock (_trava) return _primeiro is not null; } }

    /// <summary>Registra um pedido em <paramref name="agora"/>: o scan passa a vencer <c>espera</c> depois dele (limitado pelo teto).</summary>
    public void Solicitar(TimeSpan agora)
    {
        lock (_trava)
        {
            _primeiro ??= agora;
            _ultimo = agora;
        }
    }

    /// <summary>Quanto falta pro scan vencer (zero = já venceu); null = nada pendente.</summary>
    public TimeSpan? TempoAteVencer(TimeSpan agora)
    {
        lock (_trava) return Vencimento() is { } v ? (v > agora ? v - agora : TimeSpan.Zero) : null;
    }

    /// <summary>Se o scan venceu, zera o que estava pendente e devolve true (um pedido novo depois disso começa outra rajada).</summary>
    public bool TentarColher(TimeSpan agora)
    {
        lock (_trava)
        {
            if (Vencimento() is not { } v || agora < v) return false;
            _primeiro = null;
            return true;
        }
    }

    // chamar com _trava
    private TimeSpan? Vencimento() =>
        _primeiro is { } primeiro ? Min(_ultimo + _espera, primeiro + _maxima) : null;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
