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
    private double _brilho = 1.0;   // filtro CSS brightness() no <video> da TV (não mexe no painel)
    private bool _zoom;             // true = vídeo preenche a tela cortando as bordas ("tela cheia")
    private int _seekVersion;
    private double _seekDelta;
    private int _pararVersion;
    private double _posSegundos;      // reportado pela TV, pra o celular mostrar onde está
    private double _duracaoSegundos;
    private int _seekAbsVersion;
    private double _seekAbsPos;
    private int _legendaIdx = -1;   // -1 = legenda desligada
    private int _legendaVersion;

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                filmeId = _filmeId,
                playing = _playing,
                volume = _volume,
                brilho = _brilho,
                zoom = _zoom,
                seekVersion = _seekVersion,
                seekDelta = _seekDelta,
                pararVersion = _pararVersion,
                posSegundos = _posSegundos,
                duracaoSegundos = _duracaoSegundos,
                seekAbsVersion = _seekAbsVersion,
                seekAbsPos = _seekAbsPos,
                legendaIdx = _legendaIdx,
                legendaVersion = _legendaVersion
            };
        }
    }

    public void Selecionar(int filmeId)
    {
        lock (_lock) { _filmeId = filmeId; _playing = true; _posSegundos = 0; _duracaoSegundos = 0; _legendaIdx = -1; }
    }

    /// <summary>Celular escolheu uma faixa de legenda (-1 = desligar). A TV aplica no próximo poll.</summary>
    public void SetLegenda(int idx)
    {
        lock (_lock) { _legendaVersion++; _legendaIdx = idx; }
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

    /// <summary>Brilho do vídeo (filtro <c>brightness()</c> no <c>&lt;video&gt;</c>, não o painel
    /// da TV — não dá pra mexer nele por navegador). 1.0 = normal. Persiste entre filmes.</summary>
    public void SetBrilho(double brilho)
    {
        lock (_lock) { _brilho = Math.Clamp(brilho, 0.3, 2.0); }
    }

    /// <summary>Alterna "tela cheia" na TV: o vídeo preenche a tela cortando as barras pretas
    /// (zoom / object-fit: cover). Persiste entre filmes.</summary>
    public void AlternarZoom()
    {
        lock (_lock) { _zoom = !_zoom; }
    }

    public void Seek(double deltaSegundos)
    {
        lock (_lock) { _seekVersion++; _seekDelta = deltaSegundos; }
    }

    public void Parar()
    {
        lock (_lock) { _filmeId = null; _playing = false; _pararVersion++; _posSegundos = 0; _duracaoSegundos = 0; _legendaIdx = -1; }
    }
}
