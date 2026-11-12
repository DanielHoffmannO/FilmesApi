using FilmesApi.Controllers;
using FilmesApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>As regras puras do "Ainda está assistindo?" (contador, quem zera) e do timer de sono (vencimento, o que o fim do episódio faz).</summary>
public class SonoRegrasTests
{
    // ─── "Ainda está assistindo?": o contador ───────────────────────────────────────

    [Theory]
    [InlineData(null, 3)]    // sem configuração: padrão 3
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(0, 0)]       // desligado
    [InlineData(-2, 0)]      // negativo não existe: vira desligado
    public void Limite_vem_da_configuracao_com_padrao_3(int? configurado, int esperado) =>
        Assert.Equal(esperado, AindaAssistindo.Limite(configurado));

    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]     // 3 avanços sozinha (episódios 2, 3 e 4): o próximo pede confirmação
    [InlineData(9, 3, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(50, 0, false)]   // limite 0: nunca pergunta, por mais que avance
    public void Pergunta_quando_o_contador_chega_ao_limite(int avancos, int limite, bool devida) =>
        Assert.Equal(devida, AindaAssistindo.DevePerguntar(avancos, limite));

    [Fact]
    public void Contar_um_avanco_soma_um_e_nao_estoura()
    {
        Assert.Equal(1, AindaAssistindo.AposAvancoAutomatico(0));
        Assert.Equal(4, AindaAssistindo.AposAvancoAutomatico(3));
        Assert.Equal(int.MaxValue, AindaAssistindo.AposAvancoAutomatico(int.MaxValue));
    }

    // ─── quem zera: comando de uma pessoa (celular ou tecla na TV); o que a TV faz sozinha não conta ───

    [Theory]
    // o mesmo /selecionar é chamado pelo celular, pelo D-pad e pelo avanço da contagem: só o avanço (?auto=1) conta +1
    [InlineData("Selecionar", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("Selecionar", false, false, EfeitoNaPresenca.Zerar)]
    [InlineData("Selecionar", true, true, EfeitoNaPresenca.ContarAvanco)]
    // parar: Voltar na TV / botão do celular zera; o eco da TV fechando o player por ordem do servidor (nada tocando) não
    [InlineData("Parar", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("Parar", false, false, EfeitoNaPresenca.Nenhum)]
    // comandos do controle
    [InlineData("TogglePlayPause", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("SetVolume", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("Seek", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("SeekAbsoluto", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("SetLegenda", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("SetAudio", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("PularAbertura", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("ProximoManual", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("ProximoCancelar", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("ProximoAceita", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("Sono", false, true, EfeitoNaPresenca.Zerar)]
    [InlineData("Continuar", false, true, EfeitoNaPresenca.Zerar)]
    // tecla apertada na TV
    [InlineData("Interacao", false, true, EfeitoNaPresenca.Zerar)]
    // o que a TV faz sozinha, e só leituras
    [InlineData("ReportarPosicao", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("ProximoOferece", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("ProximoAuto", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("AberturaPulada", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("LegendaDaTv", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("SonoFim", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("PresencaPergunta", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("ObterEstado", false, true, EfeitoNaPresenca.Nenhum)]
    [InlineData("AcaoQueNaoExiste", false, true, EfeitoNaPresenca.Nenhum)]   // o que não está na lista não zera nada
    public void Quem_zera_a_conta(string acao, bool avancoAutomatico, bool haFilme, EfeitoNaPresenca esperado) =>
        Assert.Equal(esperado, InteracaoHumana.Efeito(acao, avancoAutomatico, haFilme));

    // ─── timer de sono ─────────────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Vencimento_e_agora_mais_os_minutos_e_zero_ou_negativo_nao_arma()
    {
        Assert.Equal(T0.AddMinutes(15), TimerDeSono.Vencimento(15, T0));
        Assert.Null(TimerDeSono.Vencimento(0, T0));
        Assert.Null(TimerDeSono.Vencimento(-5, T0));
    }

    [Fact]
    public void Vencimento_tem_teto_de_12_horas()
    {
        Assert.Equal(T0.AddHours(12), TimerDeSono.Vencimento(100_000, T0));
        Assert.Equal(T0.AddMinutes(720), TimerDeSono.Vencimento(720, T0));
    }

    [Fact]
    public void Venceu_no_instante_exato_e_depois_mas_nunca_sem_timer()
    {
        var v = T0.AddMinutes(30);
        Assert.False(TimerDeSono.Venceu(v, T0.AddMinutes(29).AddSeconds(59)));
        Assert.True(TimerDeSono.Venceu(v, v));
        Assert.True(TimerDeSono.Venceu(v, v.AddHours(5)));
        Assert.False(TimerDeSono.Venceu(null, v.AddYears(1)));
    }

    [Fact]
    public void Restante_arredonda_pra_cima_nunca_fica_negativo_e_e_nulo_sem_timer()
    {
        var v = T0.AddMinutes(15);
        Assert.Equal(900, TimerDeSono.RestanteSegundos(v, T0));
        Assert.Equal(900, TimerDeSono.RestanteSegundos(v, T0.AddMilliseconds(200)));   // 899,8 s -> 900
        Assert.Equal(1, TimerDeSono.RestanteSegundos(v, v.AddMilliseconds(-100)));
        Assert.Equal(0, TimerDeSono.RestanteSegundos(v, v));
        Assert.Equal(0, TimerDeSono.RestanteSegundos(v, v.AddMinutes(3)));
        Assert.Null(TimerDeSono.RestanteSegundos(null, T0));
    }

    [Theory]
    [InlineData(false, false, NoFimDoEpisodio.Avancar)]
    [InlineData(false, true, NoFimDoEpisodio.Perguntar)]
    [InlineData(true, false, NoFimDoEpisodio.Dormir)]
    [InlineData(true, true, NoFimDoEpisodio.Dormir)]   // quem vai dormir não precisa de pergunta
    public void O_fim_do_episodio_dorme_pergunta_ou_avanca(bool sonoAoFim, bool perguntaDevida, NoFimDoEpisodio esperado) =>
        Assert.Equal(esperado, TimerDeSono.DecidirNoFim(sonoAoFim, perguntaDevida));

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, true)]    // sono ao fim armado
    [InlineData(false, true, false, false, true)]    // sono já disparou
    [InlineData(false, false, true, false, true)]    // pergunta devida (o próximo não começa sozinho)
    [InlineData(false, false, false, true, true)]    // pergunta na tela
    public void Segura_o_avanco_quando_o_proximo_nao_vai_comecar_sozinho(bool aoFim, bool disparou, bool devida, bool pendente, bool segura) =>
        Assert.Equal(segura, TimerDeSono.SeguraOAvanco(aoFim, disparou, devida, pendente));
}

/// <summary>O estado do player com o sono e o "Ainda está assistindo?" (relógio injetado).</summary>
public class SonoEstadoTests
{
    private sealed class Relogio { public DateTime Agora = new(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc); }

    private static IConfiguration Cfg(string? limite) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AindaAssistindoAposAvancos"] = limite }).Build();

    private static PlayerStateService Novo(Relogio r, string? limite = null) => new(() => r.Agora, limite is null ? null : Cfg(limite));

    private static void Avancos(PlayerStateService s, int n) { for (var i = 0; i < n; i++) s.RegistrarAvancoAutomatico(); }

    [Fact]
    public void Estado_inicial_sem_sono_sem_pergunta_e_o_fim_do_episodio_avanca()
    {
        dynamic s = Novo(new Relogio()).Snapshot();

        Assert.False(s.sono.ativo);
        Assert.Null(s.sono.restanteSegundos);
        Assert.Null(s.sono.ate);
        Assert.False(s.sono.aoFim);
        Assert.False(s.sono.disparou);
        Assert.Equal(0, s.sono.versao);
        Assert.Equal(0, s.presenca.avancos);
        Assert.Equal(3, s.presenca.limite);
        Assert.False(s.presenca.devida);
        Assert.False(s.presenca.pendente);
        Assert.Equal("avancar", s.noFim);
    }

    // ─── timer em minutos ─────────────────────────────────────────────────────────

    [Fact]
    public void Sono_em_minutos_mostra_a_contagem_e_ao_vencer_para_o_player()
    {
        var r = new Relogio();
        var svc = Novo(r);
        svc.Selecionar(7);
        dynamic antes = svc.Snapshot();

        svc.DefinirSono(15, aoFimDoEpisodio: false);
        dynamic a = svc.Snapshot();
        Assert.True(a.sono.ativo);
        Assert.Equal(900, a.sono.restanteSegundos);
        Assert.Equal(r.Agora.AddMinutes(15).ToString("O"), a.sono.ate);
        Assert.Equal(7, a.filmeId);

        r.Agora = r.Agora.AddMinutes(10);
        dynamic b = svc.Snapshot();
        Assert.Equal(300, b.sono.restanteSegundos);
        Assert.Equal(7, b.filmeId);   // ainda tocando

        r.Agora = r.Agora.AddMinutes(5);   // vence
        dynamic c = svc.Snapshot();
        Assert.Null(c.filmeId);
        Assert.False(c.playing);
        Assert.Equal(antes.pararVersion + 1, c.pararVersion);   // a TV fecha o player pelo mesmo caminho do "parar" do celular
        Assert.True(c.sono.disparou);
        Assert.Equal(1, c.sono.versao);
        Assert.False(c.sono.ativo);
        Assert.Null(c.sono.restanteSegundos);

        dynamic d = svc.Snapshot();   // não dispara de novo
        Assert.Equal(c.pararVersion, d.pararVersion);
        Assert.Equal(1, d.sono.versao);
    }

    [Fact]
    public void Sono_vencido_sem_nada_tocando_so_expira_em_silencio()
    {
        var r = new Relogio();
        var svc = Novo(r);
        svc.DefinirSono(15, false);

        r.Agora = r.Agora.AddMinutes(16);
        dynamic s = svc.Snapshot();

        Assert.False(s.sono.ativo);
        Assert.False(s.sono.disparou);   // não há o que parar: nada de "Boa noite" no meio da tarde
        Assert.Equal(0, s.sono.versao);
        Assert.Equal(0, s.pararVersion);
    }

    [Fact]
    public void Cancelar_desarma_e_nada_dispara_depois()
    {
        var r = new Relogio();
        var svc = Novo(r);
        svc.Selecionar(1);
        svc.DefinirSono(15, false);

        svc.DefinirSono(0, false);   // minutos = 0: cancela
        r.Agora = r.Agora.AddHours(1);
        dynamic s = svc.Snapshot();

        Assert.False(s.sono.ativo);
        Assert.False(s.sono.disparou);
        Assert.Equal(1, s.filmeId);
    }

    [Fact]
    public void Pedido_novo_substitui_o_anterior_e_os_dois_juntos_valem_o_que_vier_primeiro()
    {
        var r = new Relogio();
        var svc = Novo(r);
        svc.DefinirSono(60, false);
        svc.DefinirSono(0, true);   // trocou "em 60 min" por "ao fim do episódio"
        dynamic a = svc.Snapshot();
        Assert.Null(a.sono.restanteSegundos);
        Assert.True(a.sono.aoFim);

        svc.DefinirSono(30, true);   // os dois
        dynamic b = svc.Snapshot();
        Assert.Equal(1800, b.sono.restanteSegundos);
        Assert.True(b.sono.aoFim);
        Assert.True(b.sono.ativo);
    }

    [Fact]
    public void Sono_acima_do_teto_vira_12_horas()
    {
        var svc = Novo(new Relogio());
        svc.DefinirSono(1_000_000, false);
        dynamic s = svc.Snapshot();
        Assert.Equal(12 * 3600, s.sono.restanteSegundos);
    }

    // ─── ao fim do episódio ───────────────────────────────────────────────────────

    [Fact]
    public void Sono_ao_fim_manda_dormir_no_fim_do_episodio_e_a_tv_dispara()
    {
        var svc = Novo(new Relogio());
        svc.Selecionar(3);
        svc.DefinirSono(0, aoFimDoEpisodio: true);
        dynamic armado = svc.Snapshot();
        Assert.True(armado.sono.ativo);
        Assert.Equal("dormir", armado.noFim);
        Assert.Equal(3, armado.filmeId);   // não para antes do fim

        Assert.True(svc.DispararSonoAoFim());   // a TV avisa que o episódio acabou
        dynamic s = svc.Snapshot();
        Assert.Null(s.filmeId);
        Assert.True(s.sono.disparou);
        Assert.False(s.sono.aoFim);
        Assert.Equal("avancar", s.noFim);
        Assert.False(svc.DispararSonoAoFim());   // já não está armado
    }

    [Fact]
    public void Disparar_ao_fim_sem_o_sono_armado_nao_faz_nada()
    {
        var svc = Novo(new Relogio());
        svc.Selecionar(3);
        svc.DefinirSono(0, true);
        svc.DefinirSono(0, false);   // cancelou no meio do caminho

        Assert.False(svc.DispararSonoAoFim());
        dynamic s = svc.Snapshot();
        Assert.Equal(3, s.filmeId);
        Assert.False(s.sono.disparou);
    }

    [Fact]
    public void Sono_ao_fim_vale_mais_que_a_pergunta()
    {
        var svc = Novo(new Relogio());
        Avancos(svc, 3);
        dynamic s1 = svc.Snapshot();
        Assert.Equal("perguntar", s1.noFim);

        svc.DefinirSono(0, true);
        dynamic s2 = svc.Snapshot();
        Assert.Equal("dormir", s2.noFim);
    }

    // ─── interação ────────────────────────────────────────────────────────────────

    [Fact]
    public void Interacao_tira_a_mensagem_de_boa_noite_mas_nao_desarma_um_sono_ainda_por_vir()
    {
        var r = new Relogio();
        var svc = Novo(r);
        svc.Selecionar(1);
        svc.DefinirSono(1, false);
        r.Agora = r.Agora.AddMinutes(2);
        dynamic fim = svc.Snapshot();
        Assert.True(fim.sono.disparou);

        svc.RegistrarInteracao();   // alguém apertou uma tecla
        dynamic depois = svc.Snapshot();
        Assert.False(depois.sono.disparou);
        Assert.Equal(1, depois.sono.versao);   // a versão fica: o celular não avisa "Boa noite" de novo

        svc.DefinirSono(30, false);
        svc.RegistrarInteracao();   // mexer no volume faltando minutos não cancela o timer
        dynamic armado = svc.Snapshot();
        Assert.True(armado.sono.ativo);
        Assert.Equal(1800, armado.sono.restanteSegundos);
    }

    [Fact]
    public void Novo_pedido_de_sono_tira_a_mensagem_de_boa_noite()
    {
        var r = new Relogio();
        var svc = Novo(r);
        svc.Selecionar(1);
        svc.DefinirSono(1, false);
        r.Agora = r.Agora.AddMinutes(2);
        Assert.True(((dynamic)svc.Snapshot()).sono.disparou);

        svc.DefinirSono(15, false);
        Assert.False(((dynamic)svc.Snapshot()).sono.disparou);
    }

    // ─── "Ainda está assistindo?" ─────────────────────────────────────────────────

    [Fact]
    public void Depois_de_tres_avancos_sozinha_o_fim_do_episodio_pergunta()
    {
        var svc = Novo(new Relogio());
        Avancos(svc, 2);
        dynamic a = svc.Snapshot();
        Assert.Equal(2, a.presenca.avancos);
        Assert.False(a.presenca.devida);
        Assert.Equal("avancar", a.noFim);

        svc.RegistrarAvancoAutomatico();
        dynamic b = svc.Snapshot();
        Assert.True(b.presenca.devida);
        Assert.Equal("perguntar", b.noFim);
    }

    [Fact]
    public void Interacao_zera_a_conta()
    {
        var svc = Novo(new Relogio());
        Avancos(svc, 3);

        svc.RegistrarInteracao();
        dynamic s = svc.Snapshot();

        Assert.Equal(0, s.presenca.avancos);
        Assert.False(s.presenca.devida);
        Assert.Equal("avancar", s.noFim);
    }

    [Fact]
    public void Limite_vem_da_configuracao_e_zero_desliga()
    {
        var um = Novo(new Relogio(), "1");
        um.RegistrarAvancoAutomatico();
        dynamic a = um.Snapshot();
        Assert.Equal(1, a.presenca.limite);
        Assert.Equal("perguntar", a.noFim);

        var desligado = Novo(new Relogio(), "0");
        Avancos(desligado, 100);
        dynamic b = desligado.Snapshot();
        Assert.Equal(0, b.presenca.limite);
        Assert.False(b.presenca.devida);
        Assert.Equal("avancar", b.noFim);   // a TV avança pra sempre, como antes
    }

    [Fact]
    public void Pergunta_pendente_so_sai_com_continuar_novo_episodio_ou_parar()
    {
        var svc = Novo(new Relogio());
        svc.Selecionar(4);
        Avancos(svc, 3);

        svc.PerguntarSeAindaAssiste("S01E05 · Fim");
        dynamic a = svc.Snapshot();
        Assert.True(a.presenca.pendente);
        Assert.Equal("S01E05 · Fim", a.presenca.rotulo);

        svc.RegistrarInteracao();   // mexer no volume zera a conta, mas a TV continua esperando a resposta
        Assert.True(((dynamic)svc.Snapshot()).presenca.pendente);

        svc.Selecionar(5);   // o episódio novo começou
        dynamic b = svc.Snapshot();
        Assert.False(b.presenca.pendente);
        Assert.Null(b.presenca.rotulo);

        svc.PerguntarSeAindaAssiste("x");
        svc.Parar();
        Assert.False(((dynamic)svc.Snapshot()).presenca.pendente);
    }

    [Fact]
    public void Continuar_confirma_zera_a_conta_e_manda_a_tv_seguir_pelo_caminho_do_ok()
    {
        var svc = Novo(new Relogio());
        svc.Selecionar(4);
        Avancos(svc, 3);
        svc.PerguntarSeAindaAssiste("S01E05");
        dynamic antes = svc.Snapshot();

        svc.ConfirmarPresenca();
        dynamic s = svc.Snapshot();

        Assert.False(s.presenca.pendente);
        Assert.Equal(0, s.presenca.avancos);
        Assert.Equal(antes.aceitarProximoVersion + 1, s.aceitarProximoVersion);   // a TV vê e chama o irProximo()
        Assert.Equal(4, s.filmeId);   // quem troca de episódio é a TV
    }

    [Fact]
    public void Sem_nada_tocando_nao_ha_pergunta_pendente()
    {
        var svc = Novo(new Relogio());
        Avancos(svc, 3);
        svc.PerguntarSeAindaAssiste("x");
        Assert.False(((dynamic)svc.Snapshot()).presenca.pendente);
    }

    // ─── o que segura o pré-aquecimento ───────────────────────────────────────────

    [Fact]
    public void Segura_o_avanco_com_pergunta_devida_ou_pendente_ate_alguem_interagir()
    {
        var svc = Novo(new Relogio());
        Assert.False(svc.SegurandoAvanco);

        Avancos(svc, 3);
        Assert.True(svc.SegurandoAvanco);   // o próximo avanço vai pedir confirmação: não prepara nada

        svc.Selecionar(1);
        svc.PerguntarSeAindaAssiste("x");
        Assert.True(svc.SegurandoAvanco);

        svc.ConfirmarPresenca();
        Assert.False(svc.SegurandoAvanco);
    }

    [Fact]
    public void Segura_o_avanco_com_sono_ao_fim_e_depois_do_disparo_ate_alguem_interagir()
    {
        var r = new Relogio();
        var svc = Novo(r);
        svc.Selecionar(1);

        svc.DefinirSono(0, true);
        Assert.True(svc.SegurandoAvanco);
        svc.DefinirSono(0, false);
        Assert.False(svc.SegurandoAvanco);

        svc.DefinirSono(10, false);   // só o timer de minutos: o próximo episódio ainda pode vir
        Assert.False(svc.SegurandoAvanco);
        r.Agora = r.Agora.AddMinutes(10);   // vence: a TV parou
        Assert.True(svc.SegurandoAvanco);
        svc.RegistrarInteracao();
        Assert.False(svc.SegurandoAvanco);
    }
}

/// <summary>O filtro que liga cada ação do player à conta do "Ainda está assistindo?".</summary>
public class SonoFiltroTests
{
    private static ActionExecutingContext Contexto(string acao, string query = "")
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        var ctx = new ActionContext(http, new RouteData(), new ControllerActionDescriptor { ActionName = acao });
        return new ActionExecutingContext(ctx, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    private static int Avancos(PlayerStateService s) => ((dynamic)s.Snapshot()).presenca.avancos;

    [Fact]
    public void Avanco_da_tv_conta_e_selecao_de_pessoa_zera()
    {
        var estado = new PlayerStateService();
        var filtro = new InteracaoHumanaFilter(estado);

        filtro.OnActionExecuting(Contexto("Selecionar", "?auto=1"));
        filtro.OnActionExecuting(Contexto("Selecionar", "?auto=1"));
        filtro.OnActionExecuting(Contexto("Selecionar", "?auto=1"));
        Assert.Equal(3, Avancos(estado));

        filtro.OnActionExecuting(Contexto("Selecionar"));   // o celular (ou o D-pad) escolheu
        Assert.Equal(0, Avancos(estado));
    }

    [Fact]
    public void O_que_a_tv_faz_sozinha_nao_zera_e_comando_do_controle_zera()
    {
        var estado = new PlayerStateService();
        estado.Selecionar(1);
        var filtro = new InteracaoHumanaFilter(estado);
        for (var i = 0; i < 3; i++) estado.RegistrarAvancoAutomatico();

        filtro.OnActionExecuting(Contexto("ReportarPosicao"));
        filtro.OnActionExecuting(Contexto("ProximoOferece"));
        filtro.OnActionExecuting(Contexto("AberturaPulada"));
        filtro.OnActionExecuting(Contexto("ObterEstado"));
        Assert.Equal(3, Avancos(estado));

        filtro.OnActionExecuting(Contexto("SetVolume"));
        Assert.Equal(0, Avancos(estado));
    }

    [Fact]
    public void Parar_com_algo_tocando_zera_mas_o_eco_da_tv_com_o_player_ja_fechado_nao()
    {
        var estado = new PlayerStateService();
        var filtro = new InteracaoHumanaFilter(estado);
        estado.Selecionar(1);
        for (var i = 0; i < 3; i++) estado.RegistrarAvancoAutomatico();

        filtro.OnActionExecuting(Contexto("Parar"));   // Voltar na TV / botão parar
        Assert.Equal(0, Avancos(estado));

        estado.Parar();
        for (var i = 0; i < 3; i++) estado.RegistrarAvancoAutomatico();
        filtro.OnActionExecuting(Contexto("Parar"));   // o eco: a TV fechou o player porque o servidor mandou
        Assert.Equal(3, Avancos(estado));
    }

    [Fact]
    public void A_tecla_da_tv_tira_a_boa_noite()
    {
        var r = new DateTime(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc);
        var estado = new PlayerStateService(() => r);
        estado.Selecionar(1);
        estado.DefinirSono(0, true);
        estado.DispararSonoAoFim();
        Assert.True(((dynamic)estado.Snapshot()).sono.disparou);

        new InteracaoHumanaFilter(estado).OnActionExecuting(Contexto("Interacao"));

        Assert.False(((dynamic)estado.Snapshot()).sono.disparou);
    }

    [Theory]
    [InlineData(typeof(PlayerController))]
    [InlineData(typeof(SonoController))]
    public void Os_controllers_do_player_passam_pelo_filtro(Type controller) =>
        Assert.Contains(controller.GetCustomAttributes(typeof(TypeFilterAttribute), inherit: true).Cast<TypeFilterAttribute>(),
            a => a.ImplementationType == typeof(InteracaoHumanaFilter));

    [Fact]
    public void Todo_nome_de_acao_da_lista_de_comandos_existe_num_controller_do_player()
    {
        // a lista é por NOME: um erro de digitação (ou uma ação renomeada) deixaria o comando sem zerar a conta, em silêncio
        var nomes = InteracaoHumana.Comandos.Concat(["Selecionar", "Parar"]);
        foreach (var nome in nomes)
            Assert.True(typeof(PlayerController).GetMethod(nome) is not null || typeof(SonoController).GetMethod(nome) is not null, $"ação '{nome}' não existe");
    }

    [Fact]
    public void O_filtro_resolve_pelo_conteiner_de_servicos()
    {
        var sp = new ServiceCollection().AddSingleton<PlayerStateService>().BuildServiceProvider();
        var filtro = ActivatorUtilities.CreateInstance<InteracaoHumanaFilter>(sp);
        filtro.OnActionExecuting(Contexto("Selecionar", "?auto=1"));
        Assert.Equal(1, Avancos(sp.GetRequiredService<PlayerStateService>()));
    }

    [Fact]
    public void O_servico_do_player_resolve_pelo_conteiner_com_a_configuracao()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AindaAssistindoAposAvancos"] = "2" }).Build();
        var sp = new ServiceCollection().AddSingleton<IConfiguration>(cfg).AddSingleton<PlayerStateService>().BuildServiceProvider();

        Assert.Equal(2, ((dynamic)sp.GetRequiredService<PlayerStateService>().Snapshot()).presenca.limite);
    }
}

/// <summary>Sono armado ao fim, sono disparado ou pergunta pendente: nada novo é pré-aquecido pro "próximo episódio". Mesmo cenário do PreAquecerContinuarTests
/// (ffprobe/ffmpeg falsos: o ffprobe diz H.264 + AC3, que precisa de remux).</summary>
public class SonoPreAquecimentoTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    private async Task<(ScanFixture F, int Id, PreAquecerContinuarService Servico)> Cenario(PlayerStateService? estado)
    {
        var ffprobe = TestesComuns.ScriptFalso(_bin, "ffprobe.sh", """
            echo '{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720},{"index":1,"codec_type":"audio","codec_name":"ac3","channels":6,"tags":{"language":"eng"}}],"format":{"duration":"3600.0"}}'
            """);
        var ffmpeg = TestesComuns.ScriptFalso(_bin, "ffmpeg.sh", """
            for a; do ultimo="$a"; done
            sleep 0.2
            head -c 2097152 /dev/zero > "$ultimo"
            """);
        var f = new ScanFixture(ffmpeg: ffmpeg, ffprobe: ffprobe);
        _fixtures.Add(f);
        f.Arquivo("Filmes/Matrix.1999.mkv", new string('x', 4096));
        await f.ScanAsync();
        var id = await f.IdDeAsync("Filmes/Matrix.1999.mkv");
        await f.NoEscopoAsync(async sp => await sp.GetRequiredService<ProgressoService>().SalvarAsync(id, 300, 3600));
        var servico = new PreAquecerContinuarService(f.Escopos, new ConfigurationBuilder().Build(), NullLogger<PreAquecerContinuarService>.Instance, null, estado);
        return (f, id, servico);
    }

    private static async Task Espera(Func<bool> condicao, double segundos = 10)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicao() && DateTime.UtcNow < limite) await Task.Delay(50);
    }

    [Fact]
    public async Task Sem_nada_segurando_o_avanco_aquece_como_sempre()
    {
        var (f, id, servico) = await Cenario(new PlayerStateService());

        Assert.Equal(id, await servico.ExecutarUmaVezAsync());
        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
        Assert.True(File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
    }

    [Fact]
    public async Task Sono_ao_fim_armado_nao_aquece_e_cancelar_volta_ao_normal()
    {
        var estado = new PlayerStateService();
        var (f, id, servico) = await Cenario(estado);

        estado.DefinirSono(0, aoFimDoEpisodio: true);
        Assert.Null(await servico.ExecutarUmaVezAsync());
        Assert.Empty(Directory.EnumerateFiles(f.CacheRemux, "*.mp4"));

        estado.DefinirSono(0, false);
        Assert.Equal(id, await servico.ExecutarUmaVezAsync());
        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
    }

    [Fact]
    public async Task Pergunta_pendente_ou_devida_nao_aquece_ate_alguem_interagir()
    {
        var estado = new PlayerStateService();
        var (f, id, servico) = await Cenario(estado);
        estado.Selecionar(99);
        for (var i = 0; i < 3; i++) estado.RegistrarAvancoAutomatico();   // devida
        Assert.Null(await servico.ExecutarUmaVezAsync());

        estado.PerguntarSeAindaAssiste("S01E05");   // pendente
        Assert.Null(await servico.ExecutarUmaVezAsync());
        Assert.Empty(Directory.EnumerateFiles(f.CacheRemux, "*.mp4"));

        estado.ConfirmarPresenca();   // alguém respondeu
        Assert.Equal(id, await servico.ExecutarUmaVezAsync());
        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
    }

    [Fact]
    public async Task Depois_que_o_sono_dispara_nao_aquece_ate_alguem_interagir()
    {
        var agora = new DateTime(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc);
        var estado = new PlayerStateService(() => agora);
        var (f, id, servico) = await Cenario(estado);
        estado.Selecionar(99);
        estado.DefinirSono(10, false);
        agora = agora.AddMinutes(11);   // venceu: a TV parou

        Assert.Null(await servico.ExecutarUmaVezAsync());
        Assert.Empty(Directory.EnumerateFiles(f.CacheRemux, "*.mp4"));

        estado.RegistrarInteracao();   // uma tecla na TV
        Assert.Equal(id, await servico.ExecutarUmaVezAsync());
        await Espera(() => File.Exists(Path.Combine(f.CacheRemux, $"{id}.mp4")));
    }
}
