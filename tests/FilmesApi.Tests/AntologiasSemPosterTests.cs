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

/// <summary>Cards sem pôster que o Daniel viu: Pingu (24) e Além da Imaginação (42) são antologias numeradas SEM SxxExx (a grade as mostra como
/// série; o enriquecimento as buscava como FILME pelo título do arquivo e não achava nada) e "Guerra dos Canudos" é filme cujo nome do arquivo
/// não casa mas o da pasta casa. Nomes reais do catálogo.</summary>
[Collection("NomesGlobais")]
public class AntologiasSemPosterTests : IDisposable
{
    private const string PastaPingu = "extra/VIATORRENTS.COM - Pingu";
    private const string PastaAlem = "extra/Alem da Imaginação";
    private const string Canudos = "extra/Guerra de Canudos (1997)/Guerra dos Canudos (1997) DVDRip Oldies.mp4";

    private readonly SqliteConnection _conexao = new("Data Source=:memory:");
    private readonly ServiceProvider _sp;
    private readonly Fabrica _http = new();

    public AntologiasSemPosterTests()
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
                var q = Uri.UnescapeDataString(req.RequestUri!.Query);
                // TMDB de mentira: só estes três existem; "Guerra dos Canudos" (com "dos") não existe, "Guerra de Canudos" sim
                var corpo = url.Contains("/search/tv") && q.Contains("query=Pingu")
                        ? """{"results":[{"id":9262,"name":"Pingu","original_name":"Pingu","overview":"Um pinguim.","poster_path":"/pingu.jpg"}]}"""
                    : url.Contains("/search/tv") && q.Contains("query=Além da Imaginação")
                        ? """{"results":[{"id":6357,"name":"Além da Imaginação","original_name":"The Twilight Zone","overview":"Antologia.","poster_path":"/tz.jpg"}]}"""
                    : url.Contains("/search/movie") && q.Contains("query=Guerra de Canudos")
                        ? """{"results":[{"id":55555,"title":"Guerra de Canudos","original_title":"Guerra de Canudos","overview":"Sertão.","poster_path":"/gc.jpg","release_date":"1997-09-01"}]}"""
                    : """{"results":[]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) });
            }
        }
    }

    private MetadataService Servico()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TmdbApiKey"] = "k" }).Build();
        return new MetadataService(_sp.GetRequiredService<IServiceScopeFactory>(), new TmdbService(_http, cfg, NullLogger<TmdbService>.Instance),
            NullLogger<MetadataService>.Instance);
    }

    private static string[] Pingu() =>
        Enumerable.Range(1, 24).Select(n => $"{PastaPingu}/{n:00}- Pingu e o episodio {n} - VIATORRENTS.COM .mp4").ToArray();
    private static string[] Alem() =>
        Enumerable.Range(1, 42).Select(n => $"{PastaAlem}/{n:00} Fusao Fria {n} .mp4").ToArray();

    private void Semear(IEnumerable<string> caminhos, bool jaProcessado = false)
    {
        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var p in caminhos)
            db.Filmes.Add(new Filme { Titulo = Path.GetFileNameWithoutExtension(p), ArquivoPath = p, MetadadosEm = jaProcessado ? DateTime.UtcNow : null });
        db.SaveChanges();
    }

    [Fact]
    public void Sem_a_contagem_da_pasta_a_antologia_parece_filme_com_ela_e_episodio()
    {
        var caminhos = Alem();   // "36 Fusao Fria .mp4": número solto, sem hífen nem SxxExx (o Pingu tem "13- ", que já basta)
        Assert.False(MediaNomeParser.EhEpisodio(caminhos[0]));
        Assert.True(MediaNomeParser.EhEpisodio(caminhos[0], MediaNomeParser.ContarNumeradosPorPasta(caminhos)));
    }

    [Fact]
    public async Task Pingu_e_buscado_como_serie_pelo_nome_da_pasta_e_ganha_poster()
    {
        Semear(Pingu());
        var svc = Servico();
        while (await svc.ProcessarLoteAsync(CancellationToken.None) > 0) { }

        Assert.Single(_http.Urls, u => u.Contains("/search/tv"));   // 1 busca por série, não 24
        Assert.DoesNotContain(_http.Urls, u => u.Contains("/search/movie"));
        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.All(await db.Filmes.AsNoTracking().ToListAsync(), f =>
        {
            Assert.Equal(9262, f.TmdbId);
            Assert.EndsWith("/pingu.jpg", f.PosterUrl);
            Assert.Equal("Pingu", f.SerieTitulo);
        });
    }

    [Fact]
    public async Task Alem_da_Imaginacao_com_acento_a_mais_na_pasta_ganha_poster_pelas_42_faixas()
    {
        Semear(Alem());
        var svc = Servico();
        while (await svc.ProcessarLoteAsync(CancellationToken.None) > 0) { }

        Assert.Single(_http.Urls, u => u.Contains("/search/tv"));
        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var todos = await db.Filmes.AsNoTracking().ToListAsync();
        Assert.Equal(42, todos.Count);
        Assert.All(todos, f => Assert.Equal(6357, f.TmdbId));
    }

    [Fact]
    public async Task Antologia_que_ja_passou_sem_match_como_filme_volta_pra_fila_e_casa_como_serie()
    {
        Semear(Pingu().Concat(Alem()), jaProcessado: true);   // como está em produção: processados, sem tmdbId nem SerieTitulo
        var svc = Servico();
        while (await svc.ProcessarLoteAsync(CancellationToken.None) > 0) { }

        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var todos = await db.Filmes.AsNoTracking().ToListAsync();
        Assert.Equal(66, todos.Count);
        Assert.All(todos, f => Assert.NotNull(f.TmdbId));
    }

    [Fact]
    public async Task Guerra_dos_Canudos_casa_pelo_nome_da_pasta_quando_o_do_arquivo_nao_acha()
    {
        Semear([Canudos]);
        var svc = Servico();
        await svc.ProcessarLoteAsync(CancellationToken.None);

        using var db = _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var f = Assert.Single(await db.Filmes.AsNoTracking().ToListAsync());
        Assert.Equal(55555, f.TmdbId);
        Assert.EndsWith("/gc.jpg", f.PosterUrl);
    }
}
