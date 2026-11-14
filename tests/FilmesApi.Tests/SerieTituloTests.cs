using FilmesApi.Data;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary>O nome da série na tela vem do TMDB (<c>SerieTitulo</c>, em português) e não da pasta; episódio sem ele mantém o nome da pasta.</summary>
public class SerieTituloTests : IDisposable
{
    private readonly ScanFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Tela_mostra_o_nome_da_serie_do_tmdb_no_lugar_do_da_pasta()
    {
        _f.Arquivo("Jane the Virgin/S01E01.mkv");
        _f.Arquivo("Jane the Virgin/S01E02.mkv");
        _f.Arquivo("Outra Serie/S01E01.mkv");
        await _f.ScanAsync();

        await _f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            foreach (var filme in await db.Filmes.Where(x => x.ArquivoPath!.StartsWith("Jane")).ToListAsync())
                filme.SerieTitulo = "Jane a Virgem";
            await db.SaveChangesAsync();
            return 0;
        });

        var tela = await _f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterTelaAsync("all", "all", null));

        Assert.Contains(tela.Series, s => s.Nome == "Jane a Virgem" && s.Episodios.Count == 2);
        Assert.Contains(tela.Series, s => s.Nome == "Outra Serie");           // sem SerieTitulo: nome da pasta
        Assert.DoesNotContain(tela.Series, s => s.Nome == "Jane the Virgin");
    }

    [Fact]
    public async Task Chave_da_serie_nao_muda_com_o_nome_do_tmdb()
    {
        _f.Arquivo("Modern Family/S01E01.mkv");
        await _f.ScanAsync();
        var antes = (await _f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterTelaAsync("all", "all", null))).Series.Single().Chave;

        await _f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Filmes.SingleAsync()).SerieTitulo = "Família Moderna";
            await db.SaveChangesAsync();
            return 0;
        });

        var serie = (await _f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterTelaAsync("all", "all", null))).Series.Single();
        Assert.Equal(antes, serie.Chave);
        Assert.Equal("Família Moderna", serie.Nome);
    }

    [Fact]
    public async Task Busca_acha_a_serie_pelo_nome_original_do_tmdb()
    {
        _f.Arquivo("Modern Family/S01E01.mkv");
        await _f.ScanAsync();
        await _f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var filme = await db.Filmes.SingleAsync();
            filme.SerieTitulo = "Família Moderna";
            filme.TituloOriginal = "Modern Family";
            await db.SaveChangesAsync();
            return 0;
        });

        var porOriginal = await _f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterTelaAsync("all", "all", "modern family"));
        var porPortugues = await _f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterTelaAsync("all", "all", "familia moderna"));
        Assert.Single(porOriginal.Series);
        Assert.Single(porPortugues.Series);
    }
}
