using FilmesApi.Controllers;
using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary><see cref="Novidades.Montar"/>: a fileira "Novidades" — o que chegou há pouco e ainda não foi visto, com os episódios
/// da mesma série colapsados num cartão só.</summary>
public class NovidadesTests
{
    private static readonly DateTime Agora = new(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);

    private static FilmeResponse Filme(int id, string titulo, double diasAtras, bool assistido = false, bool extra = false) =>
        new(id, titulo, null, null, assistido, Agora.AddDays(-diasAtras), null, null, EhExtra: extra);

    private static FilmeResponse Ep(int id, string serie, int temp, int ep, double diasAtras, bool assistido = false, string? titulo = null) =>
        new(id, $"{serie} S{temp:00}E{ep:00}", null, null, assistido, Agora.AddDays(-diasAtras), null, null,
            EhEpisodio: true, Serie: serie, SerieChave: serie.ToLowerInvariant(), Temporada: temp, Episodio: ep,
            Rotulo: $"S{temp:00}E{ep:00}", TituloEpisodio: titulo);

    [Fact]
    public void Sem_nada_recente_nao_ha_novidade() =>
        Assert.Empty(Novidades.Montar([Filme(1, "Velho", 40), Ep(2, "Série", 1, 1, 30)], Agora));

    [Fact]
    public void Catalogo_vazio_nao_tem_novidade() =>
        Assert.Empty(Novidades.Montar([], Agora));

    [Fact]
    public void Filme_recente_entra_com_o_proprio_titulo_e_novos_igual_a_1()
    {
        var itens = Novidades.Montar([Filme(1, "Interestelar", 1)], Agora);

        var n = Assert.Single(itens);
        Assert.Equal((1, 1, "Interestelar"), (n.Filme.Id, n.Novos, n.Rotulo));
    }

    [Fact]
    public void Assistido_e_extra_nao_sao_novidade()
    {
        var todos = new List<FilmeResponse>
        {
            Filme(1, "Já vi", 1, assistido: true), Filme(2, "Trailer", 1, extra: true), Filme(3, "Novo", 1),
            Ep(10, "Série", 1, 1, 1, assistido: true),
        };

        Assert.Equal([3], Novidades.Montar(todos, Agora).Select(n => n.Filme.Id));
    }

    [Fact]
    public void Janela_de_dias_corta_o_que_ja_e_velho()
    {
        var todos = new List<FilmeResponse> { Filme(1, "Dentro", 13.9), Filme(2, "Fora", 14.1) };

        Assert.Equal([1], Novidades.Montar(todos, Agora, janelaDias: 14).Select(n => n.Filme.Id));
        Assert.Equal([1, 2], Novidades.Montar(todos, Agora, janelaDias: 30).Select(n => n.Filme.Id));
        Assert.Equal([1, 2], Novidades.Montar(todos, Agora, janelaDias: 0).Select(n => n.Filme.Id));   // 0 = sem corte
    }

    [Fact]
    public void Episodios_da_mesma_serie_viram_um_cartao_que_aponta_pro_mais_novo()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(11, "Seriado Ficticio", 2, 1, 1), Ep(13, "Seriado Ficticio", 2, 3, 1), Ep(12, "Seriado Ficticio", 2, 2, 1),
        };

        var n = Assert.Single(Novidades.Montar(todos, Agora));

        Assert.Equal(13, n.Filme.Id);   // o 2x3, mesmo com os ids fora de ordem
        Assert.Equal(3, n.Novos);
        Assert.Equal("Seriado Ficticio · +3 episódios novos", n.Rotulo);
    }

    [Fact]
    public void Episodio_assistido_nao_conta_nem_e_o_apontado()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(11, "Seriado Ficticio", 2, 1, 1), Ep(12, "Seriado Ficticio", 2, 2, 1), Ep(13, "Seriado Ficticio", 2, 3, 1, assistido: true),
        };

        var n = Assert.Single(Novidades.Montar(todos, Agora));

        Assert.Equal((12, 2), (n.Filme.Id, n.Novos));   // o mais novo NÃO assistido
        Assert.Equal("Seriado Ficticio · +2 episódios novos", n.Rotulo);
    }

    [Fact]
    public void Cartao_da_serie_usa_o_nome_de_exibicao_que_a_lista_mostra()
    {
        // "severance" tem nome de exibição próprio (Ruptura): o cartão fala igual à fileira de séries, não o nome cru da pasta
        var n = Assert.Single(Novidades.Montar([Ep(1, "Severance", 2, 1, 1), Ep(2, "Severance", 2, 2, 1)], Agora));

        Assert.Equal(MediaNomeParser.NomeExibicao("severance", "Severance") + " · +2 episódios novos", n.Rotulo);
    }

    [Fact]
    public void Serie_toda_assistida_nao_aparece() =>
        Assert.Empty(Novidades.Montar([Ep(1, "Série", 1, 1, 1, assistido: true), Ep(2, "Série", 1, 2, 1, assistido: true)], Agora));

    [Fact]
    public void Episodio_antigo_da_mesma_serie_nao_entra_na_contagem()
    {
        // a temporada 1 chegou há meses; só o 2x1 é novo
        var todos = new List<FilmeResponse> { Ep(1, "Série", 1, 1, 90), Ep(2, "Série", 1, 2, 90), Ep(3, "Série", 2, 1, 2) };

        var n = Assert.Single(Novidades.Montar(todos, Agora));

        Assert.Equal((3, 1), (n.Filme.Id, n.Novos));
    }

    [Fact]
    public void Um_episodio_novo_mostra_o_rotulo_dele_e_o_titulo_quando_ha()
    {
        Assert.Equal("Série · S02E05",
            Assert.Single(Novidades.Montar([Ep(1, "Série", 2, 5, 1)], Agora)).Rotulo);
        Assert.Equal("Série · S02E05 · O Apagador",
            Assert.Single(Novidades.Montar([Ep(1, "Série", 2, 5, 1, titulo: "O Apagador")], Agora)).Rotulo);
    }

    [Fact]
    public void Titulo_do_episodio_que_o_rotulo_ja_mostra_nao_se_repete()
    {
        var f = Ep(1, "Série", 2, 5, 1, titulo: "S02E05") with { Rotulo = "Ep 5 · O Apagador", TituloEpisodio = "O Apagador" };

        Assert.Equal("Série · Ep 5 · O Apagador", Assert.Single(Novidades.Montar([f], Agora)).Rotulo);
    }

    [Fact]
    public void Series_diferentes_viram_cartoes_diferentes_do_mais_novo_pro_mais_velho()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(1, "Antiga", 1, 1, 5), Ep(2, "Antiga", 1, 2, 5),
            Filme(3, "Filme do meio", 3),
            Ep(4, "Recente", 1, 1, 1),
        };

        var itens = Novidades.Montar(todos, Agora);

        Assert.Equal([4, 3, 2], itens.Select(n => n.Filme.Id));
        Assert.Equal([1, 1, 2], itens.Select(n => n.Novos));
    }

    [Fact]
    public void Cartao_da_serie_ordena_pela_data_do_episodio_mais_recente()
    {
        // série com um episódio de 10 dias e outro de ontem: o cartão vale "ontem" e passa na frente do filme de 3 dias
        var todos = new List<FilmeResponse> { Ep(1, "Série", 1, 1, 10), Ep(2, "Série", 1, 2, 1), Filme(3, "Filme", 3) };

        Assert.Equal([2, 3], Novidades.Montar(todos, Agora).Select(n => n.Filme.Id));
    }

    [Fact]
    public void Limite_vale_depois_de_colapsar()
    {
        var todos = new List<FilmeResponse> { Filme(100, "A", 1), Filme(101, "B", 2), Filme(102, "C", 3) };
        todos.AddRange(Enumerable.Range(1, 6).Select(e => Ep(e, "Série", 1, e, 0.5)));   // 6 episódios = 1 cartão

        var itens = Novidades.Montar(todos, Agora, limite: 2);

        Assert.Equal([6, 100], itens.Select(n => n.Filme.Id));
        Assert.Equal(4, Novidades.Montar(todos, Agora, limite: 20).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Limite_nao_positivo_devolve_vazio(int limite) =>
        Assert.Empty(Novidades.Montar([Filme(1, "A", 1)], Agora, limite));

    [Fact]
    public void Empate_de_data_desempata_por_id_do_maior_pro_menor()
    {
        var todos = new List<FilmeResponse> { Filme(1, "A", 1), Filme(2, "B", 1), Filme(3, "C", 1) };

        Assert.Equal([3, 2, 1], Novidades.Montar(todos, Agora).Select(n => n.Filme.Id));
    }

    [Fact]
    public void Serie_sem_chave_agrupa_pelo_nome_cru()
    {
        var a = Ep(1, "Sem Chave", 1, 1, 1) with { SerieChave = null };
        var b = Ep(2, "Sem Chave", 1, 2, 1) with { SerieChave = null };

        var n = Assert.Single(Novidades.Montar([a, b], Agora));

        Assert.Equal((2, "Sem Chave · +2 episódios novos"), (n.Filme.Id, n.Rotulo));
    }

    // ─── ponta a ponta: o endpoint GET /api/catalogo/novidades, com banco de verdade ───────────────

    [Fact]
    public async Task Endpoint_devolve_o_que_foi_adicionado_recentemente_sem_o_assistido()
    {
        using var f = new ScanFixture();
        var itens = await f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var agora = DateTime.UtcNow;
            db.Filmes.AddRange(
                new Filme { Titulo = "Filme novo", ArquivoPath = "Filmes/Filme.Novo.2024.mkv", DataAdicionado = agora.AddHours(-2) },
                new Filme { Titulo = "Filme velho", ArquivoPath = "Filmes/Filme.Velho.2010.mkv", DataAdicionado = agora.AddDays(-60) },
                new Filme { Titulo = "Filme visto", ArquivoPath = "Filmes/Filme.Visto.2024.mkv", DataAdicionado = agora.AddHours(-3), Assistido = true },
                new Filme { Titulo = "Show", ArquivoPath = "Series/Show.S01E01.mkv", DataAdicionado = agora.AddHours(-1) },
                new Filme { Titulo = "Show", ArquivoPath = "Series/Show.S01E02.mkv", DataAdicionado = agora.AddHours(-1) });
            await db.SaveChangesAsync();

            var controller = new NovidadesController(sp.GetRequiredService<FilmeService>(), new ConfigurationBuilder().Build());
            var ok = Assert.IsType<OkObjectResult>(await controller.ObterNovidades(20));
            return Assert.IsType<List<ItemNovidade>>(ok.Value);
        });

        Assert.Equal(2, itens.Count);
        Assert.Contains(itens, n => n.Rotulo == "Filme novo" && n.Novos == 1);
        Assert.Contains(itens, n => n.Novos == 2 && n.Rotulo.EndsWith("+2 episódios novos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Endpoint_respeita_o_limite_e_a_janela_configurada()
    {
        using var f = new ScanFixture();
        var (limitado, semJanela) = await f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var agora = DateTime.UtcNow;
            db.Filmes.AddRange(
                new Filme { Titulo = "A", ArquivoPath = "Filmes/A.2024.mkv", DataAdicionado = agora.AddDays(-1) },
                new Filme { Titulo = "B", ArquivoPath = "Filmes/B.2024.mkv", DataAdicionado = agora.AddDays(-2) },
                new Filme { Titulo = "C", ArquivoPath = "Filmes/C.2024.mkv", DataAdicionado = agora.AddDays(-30) });
            await db.SaveChangesAsync();

            var servico = sp.GetRequiredService<FilmeService>();
            var padrao = new NovidadesController(servico, new ConfigurationBuilder().Build());
            var larga = new NovidadesController(servico, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["NovidadesDias"] = "90" }).Build());
            var a = (List<ItemNovidade>)((OkObjectResult)await padrao.ObterNovidades(1)).Value!;
            var b = (List<ItemNovidade>)((OkObjectResult)await larga.ObterNovidades(50)).Value!;
            return (a, b);
        });

        Assert.Equal(["A"], limitado.Select(n => n.Rotulo));
        Assert.Equal(3, semJanela.Count);
    }
}
