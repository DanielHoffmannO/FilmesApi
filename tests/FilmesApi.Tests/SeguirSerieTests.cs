using FilmesApi.Data;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

/// <summary>O serviço de "Seguir série": persiste, calcula a janela sobre o catálogo de verdade e protege o que está na janela e os assistidos de hoje.</summary>
public class SeguirSerieServicoTests
{
    private static async Task<(ScanFixture Fx, SeguirSerieService Svc, string Chave, List<int> Ids)> Montar()
    {
        var fx = new ScanFixture();
        for (var e = 1; e <= 8; e++) fx.Arquivo($"Series/Show/Show.S01E{e:00}.mkv");
        await fx.ScanAsync();
        var catalogo = await fx.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ListarAsync());
        var eps = catalogo.OrderBy(f => f.Episodio).ToList();
        return (fx, fx.Servico<SeguirSerieService>(), eps[0].SerieChave!, eps.Select(f => f.Id).ToList());
    }

    private static async Task Assistir(ScanFixture fx, int id, DateTime quando) => await fx.NoEscopoAsync(async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var f = await db.Filmes.SingleAsync(x => x.Id == id);
        f.Assistido = true; f.AssistidoEm = quando;
        await db.SaveChangesAsync();
        return 0;
    });

    [Fact]
    public async Task Seguir_calcula_a_janela_de_5_e_protege_so_ela()
    {
        var (fx, svc, chave, ids) = await Montar();
        using var _ = fx;
        Assert.True(await svc.SeguirAsync(chave));

        var s = Assert.Single(svc.Seguidas);
        Assert.Equal(ids.Take(5), s.Janela);
        Assert.Equal(ids.Take(5).ToHashSet(), svc.Protegidos);
        Assert.Equal(ids.Take(5), svc.OrdemDePreparo);
    }

    [Fact]
    public async Task Assistir_empurra_a_janela_e_o_assistido_de_hoje_continua_protegido_ate_liberar()
    {
        var (fx, svc, chave, ids) = await Montar();
        using var _ = fx;
        await svc.SeguirAsync(chave);
        await Assistir(fx, ids[0], DateTime.UtcNow.AddHours(-2));    // visto há 2 h: protegido
        await Assistir(fx, ids[1], DateTime.UtcNow.AddHours(-30));   // visto há 30 h: liberado
        await svc.AtualizarAsync();

        Assert.Equal(ids.Skip(2).Take(5), Assert.Single(svc.Seguidas).Janela);
        Assert.Contains(ids[0], svc.Protegidos);
        Assert.DoesNotContain(ids[1], svc.Protegidos);
        Assert.Equal(ids.Skip(2).Take(5).Append(ids[0]).ToHashSet(), svc.Protegidos);
    }

    [Fact]
    public async Task Deixar_de_seguir_solta_tudo_e_serie_inexistente_nao_e_seguida()
    {
        var (fx, svc, chave, _) = await Montar();
        using var __ = fx;
        Assert.False(await svc.SeguirAsync("serie-que-nao-existe"));
        await svc.SeguirAsync(chave);
        Assert.True(await svc.DeixarAsync(chave));
        Assert.Empty(svc.Seguidas);
        Assert.Empty(svc.Protegidos);
        Assert.False(await svc.DeixarAsync(chave));   // já não era seguida
    }

    [Fact]
    public async Task A_lista_de_seguidas_sobrevive_a_um_restart_e_a_janela_e_configuravel()
    {
        var (fx, svc, chave, ids) = await Montar();
        using var _ = fx;
        await svc.SeguirAsync(chave, janela: 3);
        var novo = new SeguirSerieService(fx.Escopos, new ConfigurationBuilder().Build(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SeguirSerieService>.Instance);
        await novo.AtualizarAsync();   // "reiniciou": relê do banco
        Assert.Equal(ids.Take(3), Assert.Single(novo.Seguidas).Janela);
        Assert.Equal(5, novo.JanelaPadrao);   // padrão sem configuração
    }

    [Fact]
    public async Task Protegidos_do_cache_unem_o_fixado_a_mao_e_as_janelas_das_series_seguidas()
    {
        var (fx, svc, chave, ids) = await Montar();
        using var _ = fx;
        var cache = new CacheFixadoService(fx.Escopos, fx.Servico<HlsTranscodeService>(), new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CacheFixadoService>.Instance, svc);
        await cache.FixarAsync([ids[7]]);   // fixado à mão: o último episódio
        await svc.SeguirAsync(chave);
        Assert.Equal(ids.Take(5).Append(ids[7]).ToHashSet(), cache.Protegidos);
        Assert.Equal([ids[7]], cache.Fixados);   // o fixado à mão continua separado (é o que "remover" mexe)
        await svc.DeixarAsync(chave);
        Assert.Equal([ids[7]], cache.Protegidos);
    }
}
