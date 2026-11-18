using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>As regras puras de "Seguir série no cache": qual janela fica pronta e quando um episódio assistido deixa de ser protegido.</summary>
public class SeguirSerieTests
{
    private static readonly DateTime Agora = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static EpisodioSeguido Vis(int id, TimeSpan? ha = null) => new(id, true, ha is null ? null : Agora - ha, false);
    private static EpisodioSeguido Falta(int id, bool andamento = false) => new(id, false, null, andamento);

    [Fact]
    public void Janela_sao_os_5_primeiros_nao_assistidos_depois_do_ultimo_assistido()
    {
        var eps = new[] { Vis(1), Vis(2), Falta(3), Falta(4), Falta(5), Falta(6), Falta(7), Falta(8), Falta(9) };
        Assert.Equal([3, 4, 5, 6, 7], PoliticaSeguirSerie.Janela(eps, 5));
    }

    [Fact]
    public void Serie_nova_sem_nada_assistido_comeca_no_primeiro()
    {
        var eps = Enumerable.Range(1, 8).Select(i => Falta(i)).ToArray();
        Assert.Equal([1, 2, 3, 4, 5], PoliticaSeguirSerie.Janela(eps, 5));
    }

    [Fact]
    public void Episodio_em_andamento_conta_e_buraco_antigo_nao_assistido_fica_de_fora()
    {
        // 1 e 2 vistos; 3 em andamento; 4 e 5 por ver; o episódio 0 nunca visto (buraco antes do último assistido) não entra
        var eps = new[] { Falta(0), Vis(1), Vis(2), Falta(3, andamento: true), Falta(4), Falta(5), Falta(6) };
        Assert.Equal([3, 4, 5, 6], PoliticaSeguirSerie.Janela(eps, 5));
    }

    [Fact]
    public void Em_andamento_ANTES_do_ultimo_assistido_tambem_fica_na_janela()
    {
        var eps = new[] { Falta(1, andamento: true), Vis(2), Falta(3), Falta(4) };
        Assert.Equal([1, 3, 4], PoliticaSeguirSerie.Janela(eps, 5));
    }

    [Fact]
    public void Episodio_novo_no_fim_entra_na_janela_sozinho_quando_cabe()
    {
        var antes = new[] { Vis(1), Falta(2), Falta(3), Falta(4) };
        var depois = antes.Append(Falta(5)).ToArray();   // chegou no scan
        Assert.Equal([2, 3, 4], PoliticaSeguirSerie.Janela(antes, 5));
        Assert.Equal([2, 3, 4, 5], PoliticaSeguirSerie.Janela(depois, 5));
    }

    [Fact]
    public void Assistir_o_primeiro_da_janela_empurra_a_janela_pra_frente()
    {
        var eps = new[] { Vis(1), Vis(2), Falta(3), Falta(4), Falta(5), Falta(6), Falta(7), Falta(8) };
        Assert.Equal([3, 4, 5, 6, 7], PoliticaSeguirSerie.Janela(eps, 5));
        eps[2] = Vis(3, TimeSpan.FromMinutes(5));
        Assert.Equal([4, 5, 6, 7, 8], PoliticaSeguirSerie.Janela(eps, 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Janela_zero_ou_negativa_nao_segura_nada(int n) =>
        Assert.Empty(PoliticaSeguirSerie.Janela([Falta(1), Falta(2)], n));

    [Fact]
    public void Assistido_continua_protegido_por_um_dia_e_depois_e_liberado()
    {
        var eps = new[] { Vis(1, TimeSpan.FromHours(2)), Vis(2, TimeSpan.FromHours(23)), Vis(3, TimeSpan.FromHours(25)), Vis(4), Falta(5) };
        var protegidos = PoliticaSeguirSerie.RecemAssistidos(eps, Agora, TimeSpan.FromHours(24));
        Assert.Equal([1, 2], protegidos);   // 3 passou de 24 h; 4 não tem data (linha antiga): liberado; 5 não foi visto
    }

    [Fact]
    public void Padroes_sao_5_episodios_e_24_horas()
    {
        Assert.Equal(5, PoliticaSeguirSerie.JanelaPadrao);
        Assert.Equal(TimeSpan.FromHours(24), PoliticaSeguirSerie.LiberaAposPadrao);
    }
}
