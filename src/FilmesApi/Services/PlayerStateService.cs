namespace FilmesApi.Services;

/// <summary>
/// Estado global do "player da TV", controlado remotamente pelo celular (controle.html).
/// Sessão única — pensado para uma casa com uma TV. Sem brilho/zoom de propósito: essas
/// mudam o que aparece na tela da TV, e o objetivo aqui é só controle remoto (play/pause,
/// seek, volume, legenda) sem alterar em nada o player que já está estável.
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
    private int _legendaIdx = -1;   // -1 = legenda desligada
    private int _legendaVersion;
    // Oferta de "próximo episódio" que a TV mostra sozinha ao terminar um episódio (com
    // contagem regressiva de auto-play) — sem isso, quem só tem o celular na mão não sabe
    // que a oferta apareceu nem consegue confirmar/adiantar sem ir até a TV.
    private int? _proximoFilmeId;
    private string? _proximoRotulo;
    private int _aceitarProximoVersion;

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
                seekAbsPos = _seekAbsPos,
                legendaIdx = _legendaIdx,
                legendaVersion = _legendaVersion,
                proximoFilmeId = _proximoFilmeId,
                proximoRotulo = _proximoRotulo,
                aceitarProximoVersion = _aceitarProximoVersion,
            };
        }
    }

    /// <summary>Chamado tanto pelo comando remoto (celular escolheu um filme) quanto pela TV
    /// avisando "acabei de começar a tocar isso sozinha" (D-pad local) — fonte única de
    /// verdade sobre o que está tocando, não importa de onde veio o comando.</summary>
    public void Selecionar(int filmeId)
    {
        lock (_lock)
        {
            _filmeId = filmeId; _playing = true; _posSegundos = 0; _duracaoSegundos = 0; _legendaIdx = -1;
            _proximoFilmeId = null; _proximoRotulo = null;
        }
    }

    /// <summary>A TV começou a contagem regressiva de "próximo episódio" — o celular passa a
    /// mostrar o que vem a seguir e um botão pra confirmar sem precisar ir até a TV.</summary>
    public void OferecerProximo(int filmeId, string rotulo)
    {
        lock (_lock) { _proximoFilmeId = filmeId; _proximoRotulo = rotulo; }
    }

    /// <summary>Celular confirmou o próximo episódio oferecido — a TV aplica no próximo poll
    /// chamando o mesmo fluxo que usaria se alguém tivesse apertado OK localmente.</summary>
    public void AceitarProximo()
    {
        lock (_lock) { _aceitarProximoVersion++; }
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

    public void Seek(double deltaSegundos)
    {
        lock (_lock) { _seekVersion++; _seekDelta = deltaSegundos; }
    }

    /// <summary>Chamado tanto pelo comando remoto "parar" quanto pela TV avisando que o
    /// player fechou localmente (tecla Voltar) — mesma ideia do Selecionar acima.</summary>
    public void Parar()
    {
        lock (_lock)
        {
            _filmeId = null; _playing = false; _pararVersion++; _posSegundos = 0; _duracaoSegundos = 0; _legendaIdx = -1;
            _proximoFilmeId = null; _proximoRotulo = null;
        }
    }
}
