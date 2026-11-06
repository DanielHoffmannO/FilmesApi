namespace FilmesApi.Services;

/// <summary>Uma sequência de toques de avanço (« 30s ») que o controle fez: de onde saiu e onde a TV acabou. Vira candidato a "o Daniel pulou a abertura".</summary>
public record RajadaDePulo(int FilmeId, double PosAntes, double PosFinal);

/// <summary>O que o servidor sabe do episódio em exibição e a TV usa sozinha (pulo da abertura): abertura conhecida, interruptores da temporada.</summary>
public record ContextoDoFilme(double? AberturaInicio, double? AberturaFim, bool PularAbertura, bool ProximoAutomatico,
    double? InicioCreditos, string? SerieChave, int? Temporada);

/// <summary>
/// Estado global do "player da TV", controlado remotamente pelo celular (controle.html).
/// Sessão única — pensado para uma casa com uma TV. Sem brilho/zoom de propósito: essas
/// mudam o que aparece na tela da TV, e o objetivo aqui é só controle remoto (play/pause,
/// seek, volume, legenda) sem alterar em nada o player que já está estável.
/// </summary>
public class PlayerStateService(Func<DateTime>? agora = null)
{
    /// <summary>Toques de avanço com no máximo isto entre si formam UMA rajada.</summary>
    public static readonly TimeSpan JanelaDaRajada = TimeSpan.FromSeconds(6);
    /// <summary>Um report de posição só vale como "onde a TV pousou" depois disto: antes, ela pode ainda nem ter aplicado o seek.</summary>
    private static readonly TimeSpan EsperaDoPouso = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _agora = agora ?? (() => DateTime.UtcNow);
    private readonly object _lock = new();
    private sealed class RajadaEmCurso(int filmeId, double posAntes, DateTime ultimoToque)
    {
        public int FilmeId = filmeId; public double PosAntes = posAntes; public DateTime UltimoToque = ultimoToque; public double? PosFinal;
    }
    private RajadaEmCurso? _rajada;
    private ContextoDoFilme? _contexto;
    private bool _aberturaPulada;   // 1x por reprodução: o salto automático (ou o botão) já aconteceu neste episódio
    private int _avisoVersao;
    private string? _avisoTipo, _avisoTexto, _avisoAcao;
    private DateTime _avisoAte;

    /// <summary>Uma rajada de avanços terminou (6 s sem toque novo e a TV já pousou). Disparado FORA da trava.</summary>
    public event Action<RajadaDePulo>? RajadaConcluida;
    private int? _filmeId;
    private bool _playing;
    private double _volume = 1.0;
    private int _seekVersion;
    private double _seekDelta;
    private double _seekAcumulado;   // soma de TODOS os avanços/recuos já pedidos: a TV aplica a diferença entre dois polls, então dois toques no mesmo poll não se perdem
    private int _pararVersion;
    private double _posSegundos;      // reportado pela TV, pra o celular mostrar onde está
    private double _duracaoSegundos;
    private int _seekAbsVersion;
    private double _seekAbsPos;
    private int _legendaIdx = -1;   // -1 = legenda desligada
    private int _legendaVersion;
    // true quando QUEM ASSISTE escolheu (celular ou botão da TV), ligando ou desligando — daí a
    // legenda automática (áudio estrangeiro) não se mete mais até o fim do filme; só um filme novo
    // (Selecionar) zera. Trocar de dublagem recarrega o MESMO filme, então não zera.
    private bool _legendaManual;
    // Dublagem: ao contrário da legenda (puramente client-side, troca o <track> ativo), trocar
    // de faixa de áudio precisa regerar o HLS/remux no servidor (ver HlsTranscodeService —
    // o áudio vai dentro dos segments, não dá pra isolar como faixa solta). _audioIdx aqui é
    // só o "aviso" pro TV reagir; quem decide e persiste a preferência de verdade por filme é
    // o HlsTranscodeService.
    private int _audioIdx = -1;
    private int _audioVersion;
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
                seekAcumulado = _seekAcumulado,
                pararVersion = _pararVersion,
                posSegundos = _posSegundos,
                duracaoSegundos = _duracaoSegundos,
                seekAbsVersion = _seekAbsVersion,
                seekAbsPos = _seekAbsPos,
                legendaIdx = _legendaIdx,
                legendaVersion = _legendaVersion,
                legendaManual = _legendaManual,
                audioIdx = _audioIdx,
                audioVersion = _audioVersion,
                proximoFilmeId = _proximoFilmeId,
                proximoRotulo = _proximoRotulo,
                aceitarProximoVersion = _aceitarProximoVersion,
                abertura = _contexto?.AberturaInicio is double ini && _contexto.AberturaFim is double fim
                    ? new { inicio = ini, fim, pular = _contexto.PularAbertura, pulada = _aberturaPulada } : null,
                proximoAutomatico = _contexto?.ProximoAutomatico ?? false,
                inicioCreditos = _contexto?.InicioCreditos,
                aviso = _avisoVersao == 0 ? null : new { versao = _avisoVersao, tipo = _avisoTipo, texto = _avisoTexto, acao = _avisoAcao, ate = _avisoAte },
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
            _filmeId = filmeId; _playing = true; _posSegundos = 0; _duracaoSegundos = 0; _legendaIdx = -1; _legendaManual = false;
            _proximoFilmeId = null; _proximoRotulo = null;
            _contexto = null; _aberturaPulada = false; _rajada = null;
        }
    }

    /// <summary>Contexto do episódio em exibição (abertura, interruptores). Ignorado se já trocou de filme.</summary>
    public void DefinirContexto(int filmeId, ContextoDoFilme contexto)
    {
        lock (_lock) { if (_filmeId == filmeId) _contexto = contexto; }
    }

    public int? FilmeId { get { lock (_lock) return _filmeId; } }
    public double PosicaoSegundos { get { lock (_lock) return _posSegundos; } }

    /// <summary>O salto da abertura já aconteceu neste episódio (a TV não repete, mesmo que o Daniel volte pra dentro dela).</summary>
    public void MarcarAberturaPulada()
    {
        lock (_lock) { _aberturaPulada = true; }
    }

    /// <summary>Aviso curto pro controle (toast) e pra TV (uma linha): "Abertura pulada · voltar", "Pulo automático ligado · desligar"…
    /// A versão sobe a cada aviso pra o controle mostrar cada um uma vez só.</summary>
    public void Avisar(string tipo, string texto, string? acao, int segundos = 10)
    {
        lock (_lock) { _avisoVersao++; _avisoTipo = tipo; _avisoTexto = texto; _avisoAcao = acao; _avisoAte = _agora().AddSeconds(segundos); }
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
        lock (_lock) { _legendaVersion++; _legendaIdx = idx; _legendaManual = true; }
    }

    /// <summary>A TV informa a legenda que ela mesma ligou/desligou (botão vermelho ou automática). Só
    /// atualiza o que o celular mostra: NÃO mexe na versão — senão a TV aplicaria de volta o que acabou de
    /// fazer, refazendo o download da legenda.</summary>
    public void RegistrarLegendaDaTv(int idx, bool manual)
    {
        lock (_lock) { _legendaIdx = idx; _legendaManual = manual || _legendaManual; }
    }

    /// <summary>Celular escolheu uma faixa de áudio ("dublagem"). A TV aplica no próximo poll
    /// regerando o HLS/remux com essa faixa (ver <see cref="HlsTranscodeService.DefinirPreferenciaAudio"/>).</summary>
    public void SetAudio(int idx)
    {
        lock (_lock) { _audioVersion++; _audioIdx = idx; }
    }

    /// <summary>A TV informa onde está — o celular usa isso pra desenhar a barra de progresso.</summary>
    public void ReportarPosicao(double posSegundos, double duracaoSegundos)
    {
        RajadaDePulo? fechada = null;
        lock (_lock)
        {
            _posSegundos = Math.Max(0, posSegundos);
            if (duracaoSegundos > 0) _duracaoSegundos = duracaoSegundos;

            if (_rajada is { } r)
            {
                var desdeOToque = _agora() - r.UltimoToque;
                if (desdeOToque >= JanelaDaRajada) fechada = FecharRajada();
                else if (desdeOToque >= EsperaDoPouso) r.PosFinal = _posSegundos;   // o último report antes de fechar = onde a TV pousou (+ alguns s tocando)
            }
        }
        if (fechada is not null) RajadaConcluida?.Invoke(fechada);
    }

    /// <summary>Fecha a rajada atual: devolve-a se a TV chegou a pousar adiante da saída. Dentro da trava.</summary>
    private RajadaDePulo? FecharRajada()
    {
        var r = _rajada;
        _rajada = null;
        return r?.PosFinal is double fim && fim > r.PosAntes ? new RajadaDePulo(r.FilmeId, r.PosAntes, fim) : null;
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
        RajadaDePulo? fechada = null;
        lock (_lock)
        {
            _seekVersion++; _seekDelta = deltaSegundos; _seekAcumulado += deltaSegundos;

            var agora = _agora();
            if (_filmeId is int id)
            {
                if (deltaSegundos <= 0) _rajada = null;   // recuo: não é "estou pulando a abertura"
                else if (_rajada is { } r && agora - r.UltimoToque < JanelaDaRajada) { r.UltimoToque = agora; r.PosFinal = null; }
                else
                {
                    fechada = FecharRajada();   // a anterior (se tinha pousado) termina aqui
                    _rajada = new RajadaEmCurso(id, _posSegundos, agora);
                }
            }
        }
        if (fechada is not null) RajadaConcluida?.Invoke(fechada);
    }

    /// <summary>Chamado tanto pelo comando remoto "parar" quanto pela TV avisando que o
    /// player fechou localmente (tecla Voltar) — mesma ideia do Selecionar acima.</summary>
    public void Parar()
    {
        lock (_lock)
        {
            _filmeId = null; _playing = false; _pararVersion++; _posSegundos = 0; _duracaoSegundos = 0; _legendaIdx = -1; _legendaManual = false;
            _proximoFilmeId = null; _proximoRotulo = null;
            _contexto = null; _aberturaPulada = false; _rajada = null;
        }
    }
}
