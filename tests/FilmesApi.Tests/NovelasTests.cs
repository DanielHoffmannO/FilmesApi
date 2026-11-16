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

public class NovelasReleaseTests
{
    private const string Caminho = "novelas/Avenida.Brasil.720p.WEB-DL.AAC.x264-iND/Avenida.Brasil.E001.720p.WEB-DL.AAC.x264-iND.mkv";

    [Fact]
    public void Release_brasileiro_E001_vira_capitulo_da_novela_pelo_nome_da_pasta()
    {
        var c = MediaNomeParser.Classificar(Caminho, "Avenida Brasil E001 iND");
        Assert.True(c.EhEpisodio);
        Assert.True(c.EhNovela);
        Assert.Equal("Avenida Brasil", c.Serie);
        Assert.Equal(1, c.Episodio);
        Assert.Equal("Capítulo 1", c.Rotulo);
    }

    [Theory]
    [InlineData("novelas/Avenida Brasil/Avenida.Brasil.Cap.010.mkv", 10)]
    [InlineData("novelas/Avenida Brasil/Avenida Brasil - 011.mkv", 11)]
    [InlineData("novelas/Avenida Brasil/Avenida.Brasil.Ep12.720p.mkv", 12)]
    [InlineData("novelas/Avenida Brasil/Avenida.Brasil.013.720p.WEB-DL.mkv", 13)]
    [InlineData("novelas/Avenida Brasil/Avenida Brasil Episodio 14.mkv", 14)]
    public void Variantes_de_marcador(string caminho, int cap)
    {
        var c = MediaNomeParser.Classificar(caminho, "T");
        Assert.True(c.EhEpisodio);
        Assert.Equal("Avenida Brasil", c.Serie);
        Assert.Equal(cap, c.Episodio);
    }

    [Fact]
    public void Dez_capitulos_formam_uma_novela_so_na_aba_novela()
    {
        var todos = Enumerable.Range(1, 10).Reverse().Select(n =>
        {
            var p = $"novelas/Avenida.Brasil.720p.WEB-DL.AAC.x264-iND/Avenida.Brasil.E{n:000}.720p.WEB-DL.AAC.x264-iND.mkv";
            var c = MediaNomeParser.Classificar(p, "Avenida Brasil E" + n);
            return new FilmeResponse(n, "Avenida Brasil E" + n, null, p, false, DateTime.UtcNow, null, null, null, null, null,
                c.EhEpisodio, c.EhExtra, c.Serie, c.SerieChave, c.Temporada, c.Episodio, c.Rotulo, c.Pasta, c.TituloEpisodio, false, false, null, c.EhNovela);
        }).ToList();
        var t = CatalogoAgrupador.Montar(todos, [], "novela", "all", null);
        var s = Assert.Single(t.Series);
        Assert.Equal("Avenida Brasil", s.Nome);
        Assert.Equal(Enumerable.Range(1, 10), s.Episodios.Select(e => e.Episodio!.Value));
        Assert.Empty(CatalogoAgrupador.Montar(todos, [], "serie", "all", null).Series);
    }
}
