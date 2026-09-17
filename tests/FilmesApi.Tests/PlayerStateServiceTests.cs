using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// PlayerStateService é o estado compartilhado entre tv.html e controle.html — nenhum I/O,
/// só mutação protegida por lock, mas central o bastante (e já teve pelo menos uma regressão
/// de concorrência real nessa área, ver commit "Corrige shutdown/travamento no transcode HLS +
/// espelha fixes na tv.html") pra merecer cobertura própria em vez de só ser exercitado de
/// carona pelos testes de outra coisa.
/// </summary>
public class PlayerStateServiceTests
{
    [Fact]
    public void Estado_inicial_nao_tem_filme_tocando()
    {
        dynamic s = new PlayerStateService().Snapshot();
        Assert.Null(s.filmeId);
        Assert.False(s.playing);
        Assert.Equal(1.0, s.volume);
        Assert.Equal(-1, s.legendaIdx);
        Assert.Null(s.proximoFilmeId);
    }

    [Fact]
    public void Selecionar_marca_tocando_e_zera_posicao_duracao_legenda()
    {
        var svc = new PlayerStateService();
        svc.ReportarPosicao(120, 3600);
        svc.SetLegenda(2);

        svc.Selecionar(42);
        dynamic s = svc.Snapshot();

        Assert.Equal(42, s.filmeId);
        Assert.True(s.playing);
        Assert.Equal(0.0, s.posSegundos);
        Assert.Equal(0.0, s.duracaoSegundos);
        Assert.Equal(-1, s.legendaIdx);
    }

    [Fact]
    public void Selecionar_cancela_oferta_de_proximo_pendente()
    {
        // Trocar de filme no meio de uma oferta de "próximo episódio" (usuário pulou pra outro
        // filme manualmente) não pode deixar a oferta velha pendurada pro celular confirmar.
        var svc = new PlayerStateService();
        svc.OferecerProximo(7, "S02E01");

        svc.Selecionar(99);
        dynamic s = svc.Snapshot();

        Assert.Null(s.proximoFilmeId);
        Assert.Null(s.proximoRotulo);
    }

    [Fact]
    public void Parar_zera_filme_playing_posicao_e_oferta_de_proximo()
    {
        var svc = new PlayerStateService();
        svc.Selecionar(1);
        svc.ReportarPosicao(50, 100);
        svc.OferecerProximo(2, "Ep 2");

        svc.Parar();
        dynamic s = svc.Snapshot();

        Assert.Null(s.filmeId);
        Assert.False(s.playing);
        Assert.Equal(0.0, s.posSegundos);
        Assert.Equal(0.0, s.duracaoSegundos);
        Assert.Null(s.proximoFilmeId);
    }

    [Fact]
    public void Parar_incrementa_pararVersion_pra_tv_distinguir_de_um_snapshot_repetido()
    {
        var svc = new PlayerStateService();
        dynamic antes = svc.Snapshot();

        svc.Parar();
        dynamic depois = svc.Snapshot();

        Assert.Equal(antes.pararVersion + 1, depois.pararVersion);
    }

    [Fact]
    public void OferecerProximo_preenche_id_e_rotulo_sem_mexer_no_que_esta_tocando()
    {
        var svc = new PlayerStateService();
        svc.Selecionar(10);

        svc.OferecerProximo(11, "S01E02 · Título");
        dynamic s = svc.Snapshot();

        Assert.Equal(10, s.filmeId);   // continua tocando o episódio atual
        Assert.Equal(11, s.proximoFilmeId);
        Assert.Equal("S01E02 · Título", s.proximoRotulo);
    }

    [Fact]
    public void AceitarProximo_so_incrementa_a_versao_nao_troca_o_filme_sozinho()
    {
        // Quem realmente troca de filme é a TV, ao detectar essa versão mudou (ver tv.html:
        // aplicarEstadoRemoto -> irProximo()) — o backend só sinaliza "aceitou".
        var svc = new PlayerStateService();
        svc.Selecionar(1);
        svc.OferecerProximo(2, "Ep 2");
        dynamic antes = svc.Snapshot();

        svc.AceitarProximo();
        dynamic depois = svc.Snapshot();

        Assert.Equal(antes.aceitarProximoVersion + 1, depois.aceitarProximoVersion);
        Assert.Equal(1, depois.filmeId);            // ainda o mesmo filme
        Assert.Equal(2, depois.proximoFilmeId);      // oferta continua lá, intacta
    }

    [Fact]
    public void SetLegenda_incrementa_versao_a_cada_chamada_mesmo_com_o_mesmo_indice()
    {
        // A TV reage à MUDANÇA de versão, não ao valor de legendaIdx em si -- religar a
        // mesma legenda duas vezes seguidas (ex.: alternando só entre 2 faixas) precisa
        // dar 2 versões diferentes, senão a segunda troca não dispara nada na TV.
        var svc = new PlayerStateService();
        svc.SetLegenda(1);
        dynamic s1 = svc.Snapshot();

        svc.SetLegenda(1);
        dynamic s2 = svc.Snapshot();

        Assert.Equal(s1.legendaVersion + 1, s2.legendaVersion);
        Assert.Equal(1, s2.legendaIdx);
    }

    [Fact]
    public void ReportarPosicao_nao_deixa_posicao_negativa()
    {
        var svc = new PlayerStateService();
        svc.ReportarPosicao(-5, 100);
        dynamic s = svc.Snapshot();

        Assert.Equal(0.0, s.posSegundos);
    }

    [Fact]
    public void ReportarPosicao_com_duracao_zero_ou_negativa_preserva_a_duracao_anterior()
    {
        // HLS ainda convertendo reporta duração 0/instável — não pode apagar uma duração boa
        // já conhecida só porque um report pontual veio sem ela.
        var svc = new PlayerStateService();
        svc.ReportarPosicao(10, 3600);

        svc.ReportarPosicao(20, 0);
        dynamic s = svc.Snapshot();

        Assert.Equal(20.0, s.posSegundos);
        Assert.Equal(3600.0, s.duracaoSegundos);
    }

    [Fact]
    public void SeekAbsoluto_atualiza_posicao_reportada_junto_pro_celular_nao_mostrar_valor_velho()
    {
        var svc = new PlayerStateService();
        svc.ReportarPosicao(10, 100);

        svc.SeekAbsoluto(80);
        dynamic s = svc.Snapshot();

        Assert.Equal(80.0, s.seekAbsPos);
        Assert.Equal(80.0, s.posSegundos);
    }

    [Fact]
    public void SeekAbsoluto_nao_aceita_posicao_negativa()
    {
        var svc = new PlayerStateService();
        svc.SeekAbsoluto(-10);
        dynamic s = svc.Snapshot();

        Assert.Equal(0.0, s.seekAbsPos);
        Assert.Equal(0.0, s.posSegundos);
    }

    [Fact]
    public void TogglePlayPause_alterna_o_estado()
    {
        var svc = new PlayerStateService();
        dynamic inicial = svc.Snapshot();
        Assert.False(inicial.playing);

        svc.TogglePlayPause();
        Assert.True(((dynamic)svc.Snapshot()).playing);

        svc.TogglePlayPause();
        Assert.False(((dynamic)svc.Snapshot()).playing);
    }

    [Theory]
    [InlineData(-1, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(2, 1.0)]
    public void SetVolume_satura_entre_0_e_1(double entrada, double esperado)
    {
        var svc = new PlayerStateService();
        svc.SetVolume(entrada);
        dynamic s = svc.Snapshot();

        Assert.Equal(esperado, s.volume);
    }

    [Fact]
    public void Seek_aceita_delta_negativo_pra_voltar()
    {
        var svc = new PlayerStateService();
        dynamic antes = svc.Snapshot();

        svc.Seek(-10);
        dynamic depois = svc.Snapshot();

        Assert.Equal(antes.seekVersion + 1, depois.seekVersion);
        Assert.Equal(-10.0, depois.seekDelta);
    }
}
