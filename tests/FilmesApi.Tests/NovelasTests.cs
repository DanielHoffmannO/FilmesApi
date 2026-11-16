using FilmesApi.Models;
using FilmesApi.Services;

namespace FilmesApi.Tests;

public class NovelasTests
{
    private static FilmeResponse Item(int id, string caminho)
    {
        var c = MediaNomeParser.Classificar(caminho, "T");
        return new(id, "T", null, caminho, false, DateTime.UtcNow, null, null, null, null, null,
            c.EhEpisodio, c.EhExtra, c.Serie, c.SerieChave, c.Temporada, c.Episodio, c.Rotulo, c.Pasta, c.TituloEpisodio,
            false, false, null, c.EhNovela);
    }

    [Theory]
    [InlineData("novelas/Avenida Brasil/Capítulo 12.mkv", "Avenida Brasil", 12)]
    [InlineData("Novelas/Avenida Brasil/Cap. 003.mp4", "Avenida Brasil", 3)]
    [InlineData("novelas/Avenida Brasil/045 - A virada.mkv", "Avenida Brasil", 45)]
    [InlineData("novelas/Mulheres de Areia Cap 7.mkv", "Mulheres de Areia", 7)]
    public void Capitulo_em_pasta_novelas_vira_episodio(string caminho, string serie, int cap)
    {
        var c = MediaNomeParser.Classificar(caminho, "T");
        Assert.True(c.EhEpisodio);
        Assert.True(c.EhNovela);
        Assert.Equal(serie, c.Serie);
        Assert.Equal(cap, c.Episodio);
    }

    [Fact]
    public void Fora_da_pasta_novelas_nao_e_novela()
    {
        var c = MediaNomeParser.Classificar("Series/Show/Show.S01E01.mkv", "T");
        Assert.False(c.EhNovela);
    }

    [Fact]
    public void Aba_novela_so_mostra_novelas_e_as_outras_abas_nao()
    {
        var todos = new List<FilmeResponse>
        {
            Item(1, "novelas/Avenida Brasil/Capítulo 2.mkv"), Item(2, "novelas/Avenida Brasil/Capítulo 1.mkv"),
            Item(3, "Series/Show/Show.S01E01.mkv"), Item(4, "Filmes/Matrix (1999)/Matrix.mkv"),
        };
        var n = CatalogoAgrupador.Montar(todos, [], "novela", "all", null);
        var s = Assert.Single(n.Series);
        Assert.Equal([2, 1], s.Episodios.Select(e => e.Id));
        Assert.Empty(n.FilmesSoltos);

        foreach (var tipo in new[] { "all", "serie", "filme" })
        {
            var t = CatalogoAgrupador.Montar(todos, [], tipo, "all", null);
            Assert.DoesNotContain(t.Series, x => x.Episodios.Any(e => e.EhNovela));
        }
    }
}
