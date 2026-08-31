namespace FilmesApi.Services;

/// <summary>
/// Estado global do "player da TV", controlado remotamente pelo celular.
/// Sessão única — pensado para uma casa com uma TV.
/// </summary>
public class PlayerStateService
{
    private readonly object _lock = new();
    private int? _filmeId;
    private bool _playing;
    private double _volume = 1.0;
    private int _seekVersion;
    private double _seekDelta;
    private int _pararVersion;
    private double _posSegundos;      // reportado pela TV, pra o celular mostrar onde está
    private double _duracaoSegundos;
    private int _seekAbsVersion;
    private double _seekAbsPos;

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                filmeId = _filmeId,
                playing = _playing,
                volume = _volume,
                seekVersion = _seekVersion,
                seekDelta = _seekDelta,
                pararVersion = _pararVersion,
                posSegundos = _posSegundos,
                duracaoSegundos = _duracaoSegundos,
                seekAbsVersion = _seekAbsVersion,
                seekAbsPos = _seekAbsPos
            };
        }
    }

    public void Selecionar(int filmeId)
    {
        lock (_lock) { _filmeId = filmeId; _playing = true; _posSegundos = 0; _duracaoSegundos = 0; }
    }

    /// <summary>A TV informa onde está — o celular usa isso pra desenhar a barra de progresso.</summary>
    public void ReportarPosicao(double posSegundos, double duracaoSegundos)
    {
        lock (_lock)
        {
            _posSegundos = Math.Max(0, posSegundos);
            if (duracaoSegundos > 0) _duracaoSegundos = duracaoSegundos;
        }
    }

    /// <summary>Celular arrastou a barra de progresso — pula pra posição absoluta.</summary>
    public void SeekAbsoluto(double posSegundos)
    {
        lock (_lock)
        {
            _seekAbsVersion++;
            _seekAbsPos = Math.Max(0, posSegundos);
            _posSegundos = _seekAbsPos;
        }
    }

    public void TogglePlayPause()
    {
        lock (_lock) { _playing = !_playing; }
    }

    public void SetVolume(double volume)
    {
        lock (_lock) { _volume = Math.Clamp(volume, 0, 1); }
    }

    public void Seek(double deltaSegundos)
    {
        lock (_lock) { _seekVersion++; _seekDelta = deltaSegundos; }
    }

    public void Parar()
    {
        lock (_lock) { _filmeId = null; _playing = false; _pararVersion++; _posSegundos = 0; _duracaoSegundos = 0; }
    }
}
