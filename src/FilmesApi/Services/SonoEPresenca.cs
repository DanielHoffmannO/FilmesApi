namespace FilmesApi.Services;

// As regras (puras, sem relógio nem estado) do "Ainda está assistindo?" e do timer de sono. Quem guarda o estado e olha o relógio é o
// PlayerStateService (PlayerStateService.Sono.cs); quem as aplica na TV é o tv.html, que só lê o resultado no /api/player/state.

/// <summary>O que o fim de um episódio deve fazer: <c>Avancar</c> (oferece o próximo, como sempre), <c>Dormir</c> (o sono "ao fim do episódio" está
/// armado: para e diz "Boa noite") ou <c>Perguntar</c> ("Ainda está assistindo?": a TV já avançou sozinha vezes demais).</summary>
public enum NoFimDoEpisodio { Avancar, Dormir, Perguntar }

/// <summary>"Ainda está assistindo?": depois de <c>AindaAssistindoAposAvancos</c> avanços AUTOMÁTICOS seguidos (a contagem do próximo episódio chegou a zero
/// sem ninguém mexer), a TV não inicia o próximo sozinha: pergunta e fica parada até alguém interagir. Uma interação (<see cref="InteracaoHumana"/>) zera a conta.</summary>
public static class AindaAssistindo
{
    public const int LimitePadrao = 3;

    /// <summary>O N configurado: padrão 3; <c>0</c> = desligado (a TV avança pra sempre, como antes); negativo conta como 0.</summary>
    public static int Limite(int? configurado) => Math.Max(0, configurado ?? LimitePadrao);

    /// <summary>A TV já avançou sozinha <paramref name="avancos"/> vezes seguidas: o próximo avanço pede confirmação? Com N=3, os episódios 2, 3 e 4 tocam
    /// sozinhos e, no fim do 4º, pergunta. Limite 0 nunca pergunta.</summary>
    public static bool DevePerguntar(int avancos, int limite) => limite > 0 && avancos >= limite;

    /// <summary>A conta depois de mais um avanço automático (não estoura).</summary>
    public static int AposAvancoAutomatico(int avancos) => avancos == int.MaxValue ? avancos : avancos + 1;
}

/// <summary>O que uma chamada à API do player faz com a conta do <see cref="AindaAssistindo"/>.</summary>
public enum EfeitoNaPresenca { Nenhum, Zerar, ContarAvanco }

/// <summary>Quem zera a conta: um COMANDO de uma pessoa, do controle (toque no celular) ou da TV (tecla). O que a TV faz sozinha (reportar posição, oferecer o
/// próximo, pular a abertura, ligar a legenda automática, avançar quando a contagem acaba) NÃO é interação. O <c>/selecionar</c> é chamado pelas duas
/// pontas, então a TV marca o que é avanço dela (<c>?auto=1</c>); o <c>/parar</c> sem nada tocando é só o eco que a TV manda ao fechar o player por ordem do servidor.
/// A decisão é pelo NOME da ação do controller (<c>ActionName</c>); o que não está aqui não zera nada.</summary>
public static class InteracaoHumana
{
    // Controle (celular) e TV. "Interacao" = tecla apertada na TV; "Continuar" = o botão do aviso no celular.
    internal static readonly HashSet<string> Comandos = new(StringComparer.Ordinal)
    {
        "PularAbertura", "ProximoManual", "ProximoCancelar", "TogglePlayPause", "SetVolume", "Seek", "SeekAbsoluto",
        "SetLegenda", "SetAudio", "ProximoAceita", "Sono", "Continuar", "Interacao",
    };

    public static EfeitoNaPresenca Efeito(string acao, bool avancoAutomatico, bool haFilmeTocando) => acao switch
    {
        "Selecionar" => avancoAutomatico ? EfeitoNaPresenca.ContarAvanco : EfeitoNaPresenca.Zerar,
        "Parar" => haFilmeTocando ? EfeitoNaPresenca.Zerar : EfeitoNaPresenca.Nenhum,
        _ => Comandos.Contains(acao) ? EfeitoNaPresenca.Zerar : EfeitoNaPresenca.Nenhum,
    };
}

/// <summary>Timer de sono: "parar em N minutos" e "parar ao fim do episódio".</summary>
public static class TimerDeSono
{
    /// <summary>Teto do "parar em N minutos" (12 h): um valor absurdo vira isto, não um timer de meses.</summary>
    public const int TetoMinutos = 12 * 60;

    /// <summary>Quando o timer vence: <paramref name="agora"/> + N minutos; <c>null</c> se N &lt;= 0 (sem timer / cancelar).</summary>
    public static DateTime? Vencimento(int minutos, DateTime agora) => minutos <= 0 ? null : agora.AddMinutes(Math.Min(minutos, TetoMinutos));

    /// <summary>Venceu? No instante exato do vencimento já venceu.</summary>
    public static bool Venceu(DateTime? vencimento, DateTime agora) => vencimento is DateTime v && agora >= v;

    /// <summary>Segundos que faltam (arredondados pra cima, nunca negativo); <c>null</c> sem timer.</summary>
    public static int? RestanteSegundos(DateTime? vencimento, DateTime agora) =>
        vencimento is DateTime v ? (int)Math.Ceiling(Math.Max(0, (v - agora).TotalSeconds)) : null;

    /// <summary>O que o fim do episódio faz agora. O sono "ao fim" manda: quem vai dormir não precisa de pergunta.</summary>
    public static NoFimDoEpisodio DecidirNoFim(bool sonoAoFim, bool perguntaDevida) =>
        sonoAoFim ? NoFimDoEpisodio.Dormir : perguntaDevida ? NoFimDoEpisodio.Perguntar : NoFimDoEpisodio.Avancar;

    /// <summary>Com o sono "ao fim" armado, o sono já disparado ou uma pergunta devida/pendente o próximo episódio NÃO vai começar sozinho: ninguém deve
    /// prepará-lo (pré-aquecer o remux, abrir o HLS) — é CPU e VPU numa placa que já cai, por um palpite que já se sabe errado.</summary>
    public static bool SeguraOAvanco(bool sonoAoFim, bool sonoDisparou, bool perguntaDevida, bool perguntaPendente) =>
        sonoAoFim || sonoDisparou || perguntaDevida || perguntaPendente;
}
