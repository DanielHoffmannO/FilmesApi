using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>Título do episódio tirado do nome do arquivo (o que vem depois do SxxExx). Nomes REAIS do
/// acervo, com todo o ruído de release/site que eles trazem.</summary>
public class TituloEpisodioTests
{
    private static string? Titulo(string caminho) => MediaNomeParser.Classificar(caminho, "x").TituloEpisodio;

    [Theory]
    [InlineData("Series/As 3 Espiãs Demais/As 3 Espiãs Demais - S01E05 - O Apagador.mp4", "O Apagador")]
    [InlineData("Series/Futebol/S07E05 - Futebol - TORRENTMEGAFILMES.TV.mkv", "Futebol")]
    [InlineData("Series/Futurama/Futurama.S02E01.A.Flight.to.Remember.1080p.DVDRip.x265-N4U - ONDEBAIXA.COM .mkv", "A Flight to Remember")]
    public void Nomes_reais_do_acervo(string caminho, string esperado) =>
        Assert.Equal(esperado, Titulo(caminho));

    [Fact]
    public void Nome_so_de_release_nao_tem_titulo() =>
        Assert.Null(Titulo("Series/Breaking Bad/Breaking.Bad.2011.S04E09.720p.BluRay.x264.mkv"));

    [Fact]
    public void Episodio_numerado_dentro_de_pasta_de_temporada()
    {
        var c = MediaNomeParser.Classificar(
            "Modern Family 1ª Temporada (2009) BDRip 720p Dual Audio + Legendas ToTTi9 - ThePirateFilmes/01 - Pilot.mp4", "x");

        Assert.True(c.EhEpisodio);
        Assert.Equal("S01E01", c.Rotulo);
        Assert.Equal("Pilot", c.TituloEpisodio);
    }

    [Theory]
    [InlineData("Series/Hora de Aventura/Hora de Aventura - Temp 05 - Epi 08 - Tirando a Sorte.mkv", "Tirando a Sorte")]
    [InlineData("Series/Twilight/The.Twilight.Zone.[1x01].Shatterday.mkv", "Shatterday")]
    [InlineData("Series/Show/Show.S01E05.Titulo.Do.Ep-RICKSZ.mkv", "Titulo Do Ep")]
    [InlineData("Series/Show/Show S01E05 [ACESSE COMANDOTORRENTS.COM] O Capitulo.mkv", "O Capitulo")]
    [InlineData("Series/Show/Show - S01E05 - Final - www.site.com.mkv", "Final")]
    [InlineData("Series/Show/Show.S01E05E06.A.Dupla.mkv", "A Dupla")]
    public void Outros_formatos_de_marcador(string caminho, string esperado) =>
        Assert.Equal(esperado, Titulo(caminho));

    [Theory]
    [InlineData("Series/Show/Show.S01E02.2010.1080p.mkv")]     // só o ano
    [InlineData("Series/Show/Show.S01E02.mkv")]                // nada depois
    [InlineData("Series/Show/Show.S01E02 - 1080p WEB-DL.mkv")] // só ruído
    public void Sem_titulo_de_verdade_devolve_null(string caminho) => Assert.Null(Titulo(caminho));

    [Fact]
    public void Filme_avulso_nao_tem_titulo_de_episodio()
    {
        var c = MediaNomeParser.Classificar("Filmes/Matrix (1999)/Matrix.1999.1080p.BluRay.mkv", "Matrix");
        Assert.False(c.EhEpisodio);
        Assert.Null(c.TituloEpisodio);
    }

    [Fact]
    public void Rotulo_continua_o_mesmo()   // o título é um campo ao lado, não mexe no rótulo
    {
        var c = MediaNomeParser.Classificar("Series/As 3 Espiãs Demais/As 3 Espiãs Demais - S01E05 - O Apagador.mp4", "x");
        Assert.Equal("S01E05", c.Rotulo);
    }
}
