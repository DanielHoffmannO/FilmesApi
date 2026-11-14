using System.Net;
using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Override manual de tmdbId: leitura do <c>tmdb</c> no nomes.json, gravação pelo admin e
/// <c>TmdbService.BuscarPorIdAsync</c> contra um HTTP falso.</summary>
[Collection("NomesGlobais")]
public class TmdbOverrideTests : IDisposable
{
    private readonly string _dir = TestesComuns.CriarPastaTemp();
    private string Arquivo => Path.Combine(_dir, "nomes.json");

    public void Dispose()
    {
        MediaNomeParser.DefinirNomesPersonalizados(null);
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class FabricaFalsa(HttpStatusCode status, string corpo) : IHttpClientFactory
    {
        public string? UltimaUrl;
        public HttpClient CreateClient(string name) => new(new Falso(this, status, corpo));
        private sealed class Falso(FabricaFalsa f, HttpStatusCode s, string c) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                f.UltimaUrl = req.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(s) { Content = new StringContent(c) });
            }
        }
    }

    private static TmdbService Tmdb(FabricaFalsa f) => new(f,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TmdbApiKey"] = "k" }).Build(),
        NullLogger<TmdbService>.Instance);

    private NomesPersonalizadosService Nomes() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NomesPath"] = Arquivo }).Build(),
        NullLogger<NomesPersonalizadosService>.Instance);

    [Fact]
    public async Task BuscarPorId_serie_usa_tv_e_le_o_item()
    {
        var f = new FabricaFalsa(HttpStatusCode.OK, """{"id":1429,"name":"Ataque dos Titãs","original_name":"Shingeki","overview":"x","poster_path":"/p.jpg"}""");
        var r = await Tmdb(f).BuscarPorIdAsync(1429, serie: true, CancellationToken.None);
        Assert.Contains("/3/tv/1429?", f.UltimaUrl);
        Assert.Equal(1429, r!.TmdbId);
        Assert.Equal("Ataque dos Titãs", r.Titulo);
        Assert.EndsWith("/p.jpg", r.PosterUrl);
    }

    [Fact]
    public async Task BuscarPorId_filme_usa_movie()
    {
        var f = new FabricaFalsa(HttpStatusCode.OK, """{"id":7,"title":"Filme"}""");
        var r = await Tmdb(f).BuscarPorIdAsync(7, serie: false, CancellationToken.None);
        Assert.Contains("/3/movie/7?", f.UltimaUrl);
        Assert.Equal("Filme", r!.Titulo);
    }

    [Fact]
    public async Task BuscarPorId_404_devolve_null_sem_lancar()
    {
        var r = await Tmdb(new FabricaFalsa(HttpStatusCode.NotFound, "{}")).BuscarPorIdAsync(99999999, true, CancellationToken.None);
        Assert.Null(r);
    }

    [Fact]
    public async Task BuscarPorId_5xx_lanca_pro_item_ficar_pendente()
        => await Assert.ThrowsAsync<HttpRequestException>(() =>
            Tmdb(new FabricaFalsa(HttpStatusCode.BadGateway, "")).BuscarPorIdAsync(1, true, CancellationToken.None));

    [Fact]
    public async Task BuscarPorId_id_invalido_nem_chama_a_rede()
    {
        var f = new FabricaFalsa(HttpStatusCode.OK, "{}");
        Assert.Null(await Tmdb(f).BuscarPorIdAsync(0, true, CancellationToken.None));
        Assert.Null(f.UltimaUrl);
    }

    [Fact]
    public void Le_secao_tmdb()
    {
        var arq = NomesPersonalizadosService.Ler("""{ "tmdb": { "ataque dos titas": 1429 } }""");
        Assert.Equal(1429, arq.Tmdb!["ataque dos titas"]);
        Assert.Empty(NomesPersonalizadosService.Ler("{}").Tmdb ?? []);
    }

    [Theory]
    [InlineData("""{ "tmdb": { "a": 0 } }""")]
    [InlineData("""{ "tmdb": { "a": -3 } }""")]
    [InlineData("""{ "tmdb": { "a": 1.5 } }""")]
    [InlineData("""{ "tmdb": { "a": "12" } }""")]
    [InlineData("""{ "tmdb": [1] }""")]
    public void Secao_tmdb_invalida_e_rejeitada(string json)
        => Assert.Throws<FormatException>(() => NomesPersonalizadosService.Ler(json));

    [Fact]
    public void Adicionar_tmdb_grava_numero_e_aplica_o_override()
    {
        var s = Nomes();
        Assert.Equal(NomesPersonalizadosService.ResultadoAdicao.Aplicado, s.Adicionar("tmdb", "Ataque dos Titãs", "1429"));
        Assert.Contains("\"ataque dos titas\": 1429", File.ReadAllText(Arquivo));
        Assert.Equal(1429, s.TmdbOverrides["ataque dos titas"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void Adicionar_tmdb_com_id_invalido_nao_grava(string id)
    {
        var s = Nomes();
        Assert.Equal(NomesPersonalizadosService.ResultadoAdicao.SecaoInvalida, s.Adicionar("tmdb", "x", id));
        Assert.False(File.Exists(Arquivo));
    }

    [Fact]
    public void Sem_secao_tmdb_nao_ha_override()
    {
        var s = Nomes();
        s.Adicionar("apelidos", "a", "b");
        Assert.Empty(s.TmdbOverrides);
        Assert.Null(MetadataService.OverrideDe(s.TmdbOverrides, "Serie/S01E01.mkv", null));
    }

    [Fact]
    public void OverrideDe_casa_pela_chave_normalizada_do_titulo_de_busca()
    {
        var mapa = new Dictionary<string, int> { [MediaNomeParser.NormalizarBusca("Cabo do Medo")] = 555 };
        Assert.Equal(555, MetadataService.OverrideDe(mapa, "Filmes/Cabo.do.Medo.1991.1080p.mkv", null));
        Assert.Null(MetadataService.OverrideDe(mapa, "Filmes/Outro.Filme.2000.mkv", null));
    }
}
