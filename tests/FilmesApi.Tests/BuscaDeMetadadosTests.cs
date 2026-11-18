using System.Net;
using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Caracterização com nomes REAIS do catálogo que não casavam no TMDB (ruído de torrent, apelidos, pasta com nome próprio).</summary>
[Collection("NomesGlobais")]
public class BuscaDeMetadadosTests : IDisposable
{
    public void Dispose() => MediaNomeParser.DefinirNomesPersonalizados(null);

    [Theory]
    [InlineData("extra/Hora da Aventura - TORRENTMEGAFILMES/10ª Temporada - Completa - TORRENTMEGAFILMES/08 - A Primeira Investigação - TORRENTMEGAFILMES.TV.mkv", "A Primeira Investigação")]
    [InlineData("extra/Hora da Aventura - TORRENTMEGAFILMES/7ª Temporada - Completa - TORRENTMEGAFILMES/S07E38 - Pré-início - TORRENTMEGAFILMES.TV.mkv", "S07E38 Pré início")]   // SxxExx no começo do nome não é cortado (só importa na busca por título, 2ª tentativa); TORRENTMEGAFILMES TV saiu
    [InlineData("extra/VIATORRENTS.COM - Pingu/13- Pingu e a gaivota - VIATORRENTS.COM .mp4", "13 Pingu e a gaivota")]   // o "13" é o número do episódio (só sai quando o arquivo é classificado como episódio); o ruído do site saiu
    [InlineData("extra/O Agente Secreto [The Secret Agent] 10bit 4KDS 1080p AV1tester.mkv", "O Agente Secreto")]
    public void Titulo_de_busca_sem_ruido_de_torrent(string caminho, string esperado) =>
        Assert.Equal(esperado, MediaNomeParser.TituloParaBusca(caminho).Titulo);

    [Theory]
    [InlineData("extra/Hora da Aventura - TORRENTMEGAFILMES/10ª Temporada - Completa - TORRENTMEGAFILMES/08 - A Primeira Investigação - TORRENTMEGAFILMES.TV.mkv", "Hora de Aventura")]
    [InlineData("DDUVT08EP13.mp4", "Diários de Um Vampiro")]
    [InlineData("extra/FS.03.720p.Dub-TorrentDosFilmesHD/FS.03x11.720p.Dub-TorrentDosFilmesHD.mkv", "Família Soprano")]   // FS = Família Soprano (T3), não Fina Estampa
    [InlineData("extra/VIATORRENTS.COM - Pingu/13- Pingu e a gaivota - VIATORRENTS.COM .mp4", "Pingu")]
    [InlineData("novelas/Avenida.Brasil.720p.WEB-DL.AAC.x264-iND/Avenida.Brasil.E001.720p.WEB-DL.AAC.x264-iND.mkv", "Avenida Brasil")]
    public void Nome_da_serie_que_a_busca_usa_e_o_de_exibicao(string caminho, string esperado) =>
        Assert.Equal(esperado, MediaNomeParser.NomeDaSerieParaBusca(caminho));

    [Fact]
    public void Filme_em_pasta_com_nome_proprio_tem_o_nome_da_pasta_como_segunda_chance()
    {
        var caminho = "extra/Guerra de Canudos (1997)/Guerra dos Canudos (1997) DVDRip Oldies.mp4";
        Assert.Equal(("Guerra dos Canudos", 1997), MediaNomeParser.TituloParaBusca(caminho));
        Assert.Equal(("Guerra de Canudos", (int?)1997), MediaNomeParser.TituloDaPastaParaBusca(caminho));
        Assert.Null(MediaNomeParser.TituloDaPastaParaBusca("Matrix.1999.mkv"));   // na raiz: sem pasta
    }

    // ── a busca de verdade, contra um TMDB de mentira: o termo da 1ª tentativa é o nome de exibição ──
    private sealed class Fabrica : IHttpClientFactory
    {
        public List<string> Consultas = [];
        public HttpClient CreateClient(string name) => new(new Falso(this));
        private sealed class Falso(Fabrica f) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                var q = Uri.UnescapeDataString((req.RequestUri!.Query.Split("query=").LastOrDefault() ?? "").Split('&')[0]);
                f.Consultas.Add($"{(req.RequestUri.AbsolutePath.Contains("/search/tv") ? "tv" : "movie")}:{q}");
                string corpo = q switch
                {
                    "Hora de Aventura" => """{"results":[{"id":15260,"name":"Hora de Aventura","overview":"Finn e Jake","poster_path":"/h.jpg"}]}""",
                    "Diários de Um Vampiro" => """{"results":[{"id":18165,"name":"Diários de um Vampiro","overview":"x","poster_path":"/d.jpg"}]}""",
                    "Guerra de Canudos" => """{"results":[{"id":1,"title":"Guerra de Canudos","overview":"x","poster_path":"/g.jpg"}]}""",
                    _ => """{"results":[]}""",
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) });
            }
        }
    }

    private static async Task<(List<Filme> Filmes, Fabrica Http)> Rodar(params string[] caminhos)
    {
        var conexao = new SqliteConnection("Data Source=:memory:"); conexao.Open();
        var sp = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseSqlite(conexao)).BuildServiceProvider();
        var http = new Fabrica();
        using (var db = sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>())
        {
            db.Database.EnsureCreated();
            foreach (var c in caminhos) db.Filmes.Add(new Filme { Titulo = Path.GetFileNameWithoutExtension(c), ArquivoPath = c });
            db.SaveChanges();
        }
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TmdbApiKey"] = "k" }).Build();
        var svc = new MetadataService(sp.GetRequiredService<IServiceScopeFactory>(), new TmdbService(http, cfg, NullLogger<TmdbService>.Instance), NullLogger<MetadataService>.Instance);
        while (await svc.ProcessarLoteAsync(CancellationToken.None) > 0) { }
        using var db2 = sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db2.Filmes.AsNoTracking().ToListAsync(), http);
    }

    [Fact]
    public async Task Episodios_de_Hora_da_Aventura_e_DDUV_casam_pelo_nome_de_exibicao()
    {
        var (filmes, http) = await Rodar(
            "extra/Hora da Aventura - TORRENTMEGAFILMES/10ª Temporada - Completa - TORRENTMEGAFILMES/08 - A Primeira Investigação - TORRENTMEGAFILMES.TV.mkv",
            "extra/Hora da Aventura - TORRENTMEGAFILMES/10ª Temporada - Completa - TORRENTMEGAFILMES/09 - Blenanas - TORRENTMEGAFILMES.TV.mkv",
            "DDUVT08EP13.mp4");
        Assert.All(filmes, f => Assert.NotNull(f.TmdbId));
        Assert.Contains("tv:Hora de Aventura", http.Consultas);
        Assert.Contains("tv:Diários de Um Vampiro", http.Consultas);
        Assert.Single(http.Consultas, c => c == "tv:Hora de Aventura");   // uma busca pra série inteira
    }

    [Fact]
    public async Task Filme_que_nao_casa_pelo_arquivo_casa_pelo_nome_da_pasta()
    {
        var (filmes, http) = await Rodar("extra/Guerra de Canudos (1997)/Guerra dos Canudos (1997) DVDRip Oldies.mp4");
        Assert.Equal(1, filmes.Single().TmdbId);
        Assert.Equal(["movie:Guerra dos Canudos", "movie:Guerra de Canudos"], http.Consultas);
    }
}
