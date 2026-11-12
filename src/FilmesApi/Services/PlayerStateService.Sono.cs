namespace FilmesApi.Services;

// Estado do "Ainda está assistindo?" e do timer de sono. As decisões são funções puras em SonoEPresenca.cs; aqui só ficam o estado, a trava e o relógio.
// O servidor não tem timer próprio: o vencimento do sono é conferido a cada leitura do estado (Snapshot) — a TV lê a cada ~1,5 s, então o atraso é esse.
public partial class PlayerStateService
{
    private int _avancosAuto;           // avanços automáticos seguidos desde a última interação humana
    private bool _perguntaPendente;     // a TV está mostrando "Ainda está assistindo?" e esperando alguém
    private string? _perguntaRotulo;    // o episódio que começa quando confirmarem
    private DateTime? _sonoAte;         // vencimento do "parar em N minutos"
    private bool _sonoAoFim;            // "parar ao fim do episódio" armado
    private bool _sonoDisparou;         // o sono parou a TV e a mensagem "Boa noite" ainda está valendo (até alguém interagir)
    private int _sonoVersao;            // sobe a cada disparo (o celular avisa uma vez só; a TV dispensa a mensagem por versão)

    private int LimiteDeAvancos => AindaAssistindo.Limite(_config?.GetValue<int?>("AindaAssistindoAposAvancos"));
    private bool PerguntaDevida => AindaAssistindo.DevePerguntar(_avancosAuto, LimiteDeAvancos);

    /// <summary>Sono, pergunta e "o que o fim do episódio faz" no snapshot do <c>/api/player/state</c>. Chamados DENTRO da trava.</summary>
    private object SnapshotDoSono() => new
    {
        ativo = _sonoAte is not null || _sonoAoFim,
        restanteSegundos = TimerDeSono.RestanteSegundos(_sonoAte, _agora()),
        ate = _sonoAte?.ToString("O"),
        aoFim = _sonoAoFim,
        disparou = _sonoDisparou,
        versao = _sonoVersao,
    };

    private object SnapshotDaPresenca() => new
    {
        avancos = _avancosAuto,
        limite = LimiteDeAvancos,
        devida = PerguntaDevida,
        pendente = _perguntaPendente,
        rotulo = _perguntaRotulo,
    };

    private string NoFimAtual() => TimerDeSono.DecidirNoFim(_sonoAoFim, PerguntaDevida).ToString().ToLowerInvariant();

    /// <summary>Uma pessoa mexeu (comando do controle ou tecla na TV): zera a conta dos avanços automáticos e tira a mensagem "Boa noite". NÃO tira a pergunta
    /// pendente (quem responde é o "Continuar"/OK) nem desarma o sono — apertar o volume faltando 5 minutos não cancela o timer.</summary>
    public void RegistrarInteracao()
    {
        lock (_lock) { _avancosAuto = 0; _sonoDisparou = false; }
    }

    /// <summary>A TV avançou pro próximo episódio sozinha (a contagem chegou a zero sem ninguém mexer).</summary>
    public void RegistrarAvancoAutomatico()
    {
        lock (_lock) { _avancosAuto = AindaAssistindo.AposAvancoAutomatico(_avancosAuto); }
    }

    /// <summary>A TV chegou ao fim de um episódio que exige confirmação e está perguntando "Ainda está assistindo?" (o <paramref name="rotulo"/> é o do próximo).
    /// Enquanto estiver pendente, nada é pré-aquecido pro próximo.</summary>
    public void PerguntarSeAindaAssiste(string? rotulo)
    {
        lock (_lock) { if (_filmeId is not null) { _perguntaPendente = true; _perguntaRotulo = rotulo; } }
    }

    /// <summary>"Continuar" no celular: confirma a pergunta (zera a conta) e manda a TV seguir pro próximo, pelo mesmo caminho do OK local (<see cref="AceitarProximo"/>).</summary>
    public void ConfirmarPresenca()
    {
        lock (_lock) { _avancosAuto = 0; _sonoDisparou = false; _perguntaPendente = false; _perguntaRotulo = null; AceitarProximo(); }
    }

    /// <summary>Armar o sono: <paramref name="minutos"/> &gt; 0 = parar em N minutos (<see cref="TimerDeSono.TetoMinutos"/> no máximo); <paramref name="aoFimDoEpisodio"/> = não avançar
    /// pro próximo, parar ao fim do episódio. Os dois juntos valem "o que vier primeiro". Zero minutos e sem "ao fim" cancela tudo. Um pedido novo substitui o anterior.</summary>
    public void DefinirSono(int minutos, bool aoFimDoEpisodio)
    {
        lock (_lock)
        {
            _sonoAte = TimerDeSono.Vencimento(minutos, _agora());
            _sonoAoFim = aoFimDoEpisodio;
            _sonoDisparou = false;
        }
    }

    /// <summary>A TV avisa que o episódio acabou com o sono "ao fim" armado: dispara. False se já não estava armado (cancelaram no meio).</summary>
    public bool DispararSonoAoFim()
    {
        lock (_lock)
        {
            if (!_sonoAoFim) return false;
            DispararSono();
            return true;
        }
    }

    /// <summary>O player está segurando o avanço (sono ao fim armado ou disparado, pergunta devida ou pendente): ninguém deve preparar o próximo episódio.</summary>
    public bool SegurandoAvanco
    {
        get { lock (_lock) { AvaliarSono(); return TimerDeSono.SeguraOAvanco(_sonoAoFim, _sonoDisparou, PerguntaDevida, _perguntaPendente); } }
    }

    /// <summary>O timer de minutos venceu? Dispara. Dentro da trava.</summary>
    private void AvaliarSono()
    {
        if (TimerDeSono.Venceu(_sonoAte, _agora())) DispararSono();
    }

    /// <summary>Desarma o sono e, se há algo tocando, PARA o player (o mesmo <see cref="Parar"/> do botão do celular: a TV fecha o player, salva a posição e para de pedir segmentos —
    /// o transcode morre por falta de interesse) e deixa a mensagem "Boa noite" valendo. Sem nada tocando, o timer só vence em silêncio. Dentro da trava.</summary>
    private void DispararSono()
    {
        _sonoAte = null; _sonoAoFim = false;
        if (_filmeId is null) return;
        _sonoDisparou = true; _sonoVersao++;
        Parar();
    }

    /// <summary>Trocou (ou fechou) o episódio: a pergunta pendente era do episódio que acabou. Dentro da trava.</summary>
    private void AoTrocarDeFilme()
    {
        _perguntaPendente = false; _perguntaRotulo = null;
    }
}
