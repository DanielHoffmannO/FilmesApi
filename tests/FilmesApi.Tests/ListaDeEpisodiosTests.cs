using System.Net;
using System.Text.Json;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Lista de episódios e ficha da série no controle (Futurama T3E06 do print): junta catálogo + TMDB, sem TMDB a lista segue.</summary>
public class ListaDeEpisodiosTests
{
    private static FilmeResponse Ep(int id, int t, int e, string? titulo, bool visto = false, double? dur = null, double? pos = null) =>
        new(id, $"Futurama S{t:00}E{e:00}", null, $"Futurama/T{t}/e{e}.mkv", visto, DateTime.UtcNow, pos, dur, null, null, null,
            true, false, "Futurama", "futurama", t, e, $"S{t:00}E{e:00}", "Futurama", titulo);

    private static readonly List<FilmeResponse> Catalogo =
    [
        Ep(1, 1, 1, "Piloto espacial", visto: true), Ep(2, 1, 2, null),
        Ep(30, 3, 5, "Cinco", visto: true, dur: 1300), Ep(31, 3, 6, null, pos: 400), Ep(32, 3, 7, "Sete"),
    ];

    [Fact]
    public void Lista_so_a_temporada_pedida_com_visto_em_andamento_e_as_outras_temporadas()
    {
        var r = ListaDeEpisodios.Montar(Catalogo, 3, null);
        Assert.Equal(3, r.Temporada);
        Assert.Equal([(1, 2), (3, 3)], r.Temporadas.Select(t => (t.Numero, t.Episodios)));
        Assert.Equal([5, 6, 7], r.Episodios.Select(e => e.Episodio));
        Assert.True(r.Episodios[0].Assistido);
        Assert.Equal(400, r.Episodios[1].PosicaoSegundos);
        Assert.Equal(1300, r.Episodios[0].DuracaoSegundos);
        Assert.False(r.Episodios[0].DuracaoAproximada);
        Assert.Equal("Futurama", r.Serie);
    }

    [Fact]
    public void TMDB_completa_titulo_sinopse_e_duracao_aproximada_mas_nao_manda_na_duracao_real()
    {
        var tmdb = new[] { new EpisodioTmdb(5, "Outro nome", "sinopse 5", 22), new EpisodioTmdb(6, "Seis do TMDB", "sinopse 6", 23) };
        var r = ListaDeEpisodios.Montar(Catalogo, 3, tmdb);
        Assert.Equal("Cinco", r.Episodios[0].Titulo);          // o nome do arquivo ganha
        Assert.Equal(1300, r.Episodios[0].DuracaoSegundos);    // a duração que já tocou aqui ganha
        Assert.Equal("Seis do TMDB", r.Episodios[1].Titulo);   // sem nome no arquivo, vem do TMDB
        Assert.Equal(23 * 60, r.Episodios[1].DuracaoSegundos);
        Assert.True(r.Episodios[1].DuracaoAproximada);
        Assert.Equal("sinopse 6", r.Episodios[1].Sinopse);
        Assert.Null(r.Episodios[2].DuracaoSegundos);           // TMDB não conhece o 7
    }

    [Fact]
    public void Temporada_inexistente_devolve_lista_vazia_mas_mantem_as_temporadas() =>
        Assert.Empty(ListaDeEpisodios.Montar(Catalogo, 9, null).Episodios);

    private const string TvJson = """
        {"id":615,"name":"Futurama","overview":"Fry acorda no ano 3000.","first_air_date":"1999-03-28","vote_average":8.4567,
         "genres":[{"id":16,"name":"Animação"},{"id":35,"name":"Comédia"}],"number_of_seasons":11,"number_of_episodes":140,"status":"Returning Series",
         "next_episode_to_air":{"air_date":"2026-12-01","season_number":13,"episode_number":1,"name":"Volta"},
         "credits":{"cast":[{"name":"Billy West"},{"name":"Katey Sagal"},{"name":"John DiMaggio"},{"name":"Tress MacNeille"},{"name":"Maurice LaMarche"},{"name":"Phil LaMarr"}]}}
        """;

    [Fact]
    public void Leitor_da_serie_pega_ano_nota_generos_situacao_proximo_e_so_5_do_elenco()
    {
        var s = SerieInfoTmdbLeitor.Serie(JsonDocument.Parse(TvJson))!;
        Assert.Equal(615, s.TmdbId);
        Assert.Equal(1999, s.Ano);
        Assert.Equal(8.5, s.Nota);
        Assert.Equal(["Animação", "Comédia"], s.Generos);
        Assert.Equal((11, 140), (s.Temporadas, s.Episodios));
        Assert.Equal("Em exibição", s.Situacao);
        Assert.Equal(new ProximoEpisodioTmdb("2026-12-01", 13, 1, "Volta"), s.Proximo);
        Assert.Equal(5, s.Elenco.Count);
        Assert.Equal("Billy West", s.Elenco[0]);
    }

    [Fact]
    public void Leitor_tolera_campos_faltando_e_traduz_a_situacao()
    {
        var s = SerieInfoTmdbLeitor.Serie(JsonDocument.Parse("""{"id":1,"status":"Ended","vote_average":0,"next_episode_to_air":null}"""))!;
        Assert.Null(s.Ano); Assert.Null(s.Nota); Assert.Null(s.Proximo); Assert.Empty(s.Generos); Assert.Empty(s.Elenco);
        Assert.Equal("Finalizada", s.Situacao);
        Assert.Null(SerieInfoTmdbLeitor.Serie(JsonDocument.Parse("""{"nome":"sem id"}""")));
    }

    [Fact]
    public void Leitor_da_temporada_pega_titulo_sinopse_e_runtime()
    {
        var eps = SerieInfoTmdbLeitor.Temporada(JsonDocument.Parse(
            """{"episodes":[{"episode_number":1,"name":"A","overview":"x","runtime":22},{"episode_number":2,"name":"B","runtime":null},{"name":"sem numero"}]}"""));
        Assert.Equal(2, eps.Count);
        Assert.Equal(22, eps[0].Minutos);
        Assert.Null(eps[1].Minutos);
    }

    private sealed class Fabrica(Func<string, HttpResponseMessage> resp) : IHttpClientFactory
    {
        public int Chamadas;
        public HttpClient CreateClient(string name) => new(new Falso(this, resp));
        private sealed class Falso(Fabrica f, Func<string, HttpResponseMessage> r) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            { f.Chamadas++; return Task.FromResult(r(req.RequestUri!.ToString())); }
        }
    }

    private static TmdbService Tmdb(Fabrica f) =>
        new(f, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TmdbApiKey"] = "k" }).Build(), NullLogger<TmdbService>.Instance);

    [Fact]
    public async Task Ficha_fica_em_cache_e_falha_de_rede_vira_null_sem_derrubar()
    {
        var f = new Fabrica(url => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TvJson) });
        var t = Tmdb(f);
        Assert.Equal("Futurama", (await t.SerieInfoAsync(615, CancellationToken.None))!.Titulo);
        await t.SerieInfoAsync(615, CancellationToken.None);
        Assert.Equal(1, f.Chamadas);

        var cai = new Fabrica(_ => throw new HttpRequestException("sem rede"));
        Assert.Null(await Tmdb(cai).SerieInfoAsync(1, CancellationToken.None));
        var naoAcha = new Fabrica(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await Tmdb(naoAcha).TemporadaAsync(1, 1, CancellationToken.None));
    }
}
