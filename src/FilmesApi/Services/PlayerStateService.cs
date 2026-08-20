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
                pararVersion = _pararVersion
            };
        }
    }

    public void Selecionar(int filmeId)
    {
        lock (_lock) { _filmeId = filmeId; _playing = true; }
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
        lock (_lock) { _filmeId = null; _playing = false; _pararVersion++; }
    }
}
