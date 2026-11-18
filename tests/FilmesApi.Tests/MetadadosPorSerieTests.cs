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

/// <summary>Novela/série casa no TMDB pelo NOME DA SÉRIE (pasta), uma busca por série (não por episódio), e o override de tmdbId vale pra novela.</summary>
[Collection("NomesGlobais")]
public class MetadadosPorSerieTests : IDisposable
{
    private const string PastaNovela = "Avenida.Brasil.720p.WEB-DL.AAC.x264-iND";
    private static string Cap(int n) => $"novelas/{PastaNovela}/Avenida.Brasil.E{n:000}.720p.WEB-DL.AAC.x264-iND.mkv";

    private readonly SqliteConnection _conexao = new("Data Source=:memory:");
    private readonly ServiceProvider _sp;
    private readonly Fabrica _http = new();

    public MetadadosPorSerieTests()
    {
        _conexao.Open();
        _sp = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseSqlite(_conexao)).BuildServiceProvider();
        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose() { MediaNomeParser.DefinirNomesPersonalizados(null); _sp.Dispose(); _conexao.Dispose(); }

    private sealed class Fabrica : IHttpClientFactory
    {
        public List<string> Urls = [];
        public HttpClient CreateClient(string name) => new(new Falso(this));
        private sealed class Falso(Fabrica f) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                var url = req.RequestUri!.ToString();
                f.Urls.Add(url);
                // só "Avenida Brasil" existe no TMDB de mentira (o nome com release/E001 não acha nada, como no real)
                var q = req.RequestUri!.Query;
                var corpo = url.Contains("/search/tv") && (q.EndsWith("query=Avenida%20Brasil") || q.Contains("query=Avenida%20Brasil&"))
                    ? """{"results":[{"id":45815,"name":"Avenida Brasil","original_name":"Avenida Brasil","overview":"Rita se vinga.","poster_path":"/av.jpg"}]}"""
                    : url.Contains("/tv/777?") ? """{"id":777,"name":"Outra","overview":"o","poster_path":"/o.jpg"}"""
                    : """{"results":[]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) });
            }
        }
    }

    private MetadataService Servico(NomesPersonalizadosService? nomes = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TmdbApiKey"] = "k" }).Build();
        return new MetadataService(_sp.GetRequiredService<IServiceScopeFactory>(), new TmdbService(_http, cfg, NullLogger<TmdbService>.Instance),
            NullLogger<MetadataService>.Instance, null, nomes);
    }

    private void Semear(int de, int ate, bool jaProcessado = false)
    {
        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        for (var n = de; n <= ate; n++)
            db.Filmes.Add(new Filme { Titulo = $"Avenida Brasil E{n:000} iND", ArquivoPath = Cap(n), MetadadosEm = jaProcessado ? DateTime.UtcNow : null });
        db.SaveChanges();
    }

    [Fact]
    public void Titulo_de_busca_da_novela_e_o_nome_da_pasta()
    {
        var (titulo, _) = MediaNomeParser.TituloParaBusca(Cap(1), "Avenida Brasil E001 iND");
        Assert.Equal("Avenida Brasil", titulo);
    }

    [Fact]
    public async Task Trinta_e_cinco_capitulos_fazem_uma_busca_e_todos_ganham_poster_e_sinopse()
    {
        Semear(1, 35);
        var svc = Servico();
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        while (await svc.ProcessarLoteAsync(CancellationToken.None) > 0) { }
        // 1 busca (300 ms de cortesia); os outros 34 vêm do cache de série e não esperam nada (antes: 300 ms por episódio, ~10 s)
        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(5), $"demorou {relogio.Elapsed.TotalSeconds:F1} s");

        Assert.Single(_http.Urls, u => u.Contains("/search/tv"));   // 1 busca, não 35
        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var todos = await db.Filmes.AsNoTracking().ToListAsync();
        Assert.Equal(35, todos.Count);
        Assert.All(todos, f =>
        {
            Assert.Equal(45815, f.TmdbId);
            Assert.EndsWith("/av.jpg", f.PosterUrl);
            Assert.Equal("Rita se vinga.", f.Sinopse);
            Assert.Equal("Avenida Brasil", f.SerieTitulo);
        });
    }

    [Fact]
    public async Task Episodio_que_passou_sem_match_volta_pra_fila_uma_vez()
    {
        Semear(1, 3, jaProcessado: true);   // como ficaram em produção: processados, sem tmdbId
        var svc = Servico();
        await svc.ProcessarLoteAsync(CancellationToken.None);

        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.All(await db.Filmes.AsNoTracking().ToListAsync(), f => Assert.Equal(45815, f.TmdbId));
    }

    [Fact]
    public void Override_de_tmdbId_vale_pra_novela()
    {
        var overrides = new Dictionary<string, int> { [MediaNomeParser.NormalizarBusca("Avenida Brasil")] = 777 };
        Assert.Equal(777, MetadataService.OverrideDe(overrides, Cap(5), "Avenida Brasil E005 iND"));
    }

    [Fact]
    public async Task Override_busca_pelo_id_em_tv_e_aplica_a_todos()
    {
        Semear(1, 2);
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            File.WriteAllText(Path.Combine(dir, "nomes.json"), """{"tmdb":{"avenida brasil":777}}""");
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NomesPath"] = Path.Combine(dir, "nomes.json") }).Build();
            var nomes = new NomesPersonalizadosService(cfg, NullLogger<NomesPersonalizadosService>.Instance);
            nomes.Recarregar();
            var svc = Servico(nomes);
            await svc.ProcessarLoteAsync(CancellationToken.None);

            Assert.Contains(_http.Urls, u => u.Contains("/3/tv/777?"));
            using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.All(await db.Filmes.AsNoTracking().ToListAsync(), f => Assert.Equal(777, f.TmdbId));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Diagnostico_lista_quem_ficou_sem_poster_com_o_termo_buscado()
    {
        var itens = Enumerable.Range(1, 3).Select(n =>
        {
            var c = MediaNomeParser.Classificar(Cap(n), "t");
            return new FilmeResponse(n, "t", null, Cap(n), false, DateTime.UtcNow, null, null, null, null, null,
                c.EhEpisodio, c.EhExtra, c.Serie, c.SerieChave, c.Temporada, c.Episodio, c.Rotulo, c.Pasta, c.TituloEpisodio, false, false, null, c.EhNovela);
        }).ToList();
        var r = Assert.Single(DiagnosticoCatalogo.SemMetadados(itens));
        Assert.Equal("Avenida Brasil", r.Buscado);
        Assert.Equal("tv", r.Tipo);
        Assert.Equal(3, r.Itens);
    }
}
