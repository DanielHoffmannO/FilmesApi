using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>Nomes de verdade do acervo, fixados como caracterização do parser: se uma "simplificação" mudar o que a tela mostra para um
/// destes, o teste acusa. Usa só o padrão embutido (os testes de nomes.json restauram o estado global ao fim).</summary>
[Collection("NomesGlobais")]
public class NomesDoAcervoRealTests
{
    [Theory]
    [InlineData("extra/O Mentalista - 2ª Temporada Completa (720p) BLUDV.COM/O.Mentalista.S02E17.720p.WEB-DL.x264.Dublado-WWW.BLUDV.COM.mkv", "O Mentalista", 2, 17)]
    [InlineData("O.Mentalista.S02E17.720p.WEB-DL.x264.Dublado-WWW.BLUDV.COM.mkv", "O Mentalista", 2, 17)]
    [InlineData("Series/Rick and Morty/Rick.and.Morty.S09E10.1080p.WEB-DL.DUAL.5.1.mkv", "Rick and Morty", 9, 10)]
    [InlineData("Rick.and.Morty.S09E10.1080p.WEB-DL.DUAL.5.1.mkv", "Rick and Morty", 9, 10)]
    [InlineData("Series/Attack on Titan/Attack.on.Titan.S03.1080p.CR.WEB-DL.AAC2.0.H.264.DUAL/Attack.on.Titan.S03E01.1080p.CR.WEB-DL.AAC2.0.H.264.DUAL.mkv", "Attack on Titan", 3, 1)]
    [InlineData("Presidente Curtis S01E05 WEB-DL 1080p x264 DUAL 5.1.mkv", "Presidente Curtis", 1, 5)]
    [InlineData("Presidente Curtis/Presidente Curtis S01E05 WEB-DL 1080p x264 DUAL 5.1.mkv", "Presidente Curtis", 1, 5)]
    public void Episodio_de_release_real_vira_serie_temporada_e_episodio(string caminho, string serie, int temporada, int episodio)
    {
        var c = MediaNomeParser.Classificar(caminho, "x");

        Assert.True(c.EhEpisodio);
        Assert.False(c.EhExtra);
        Assert.Equal(serie, c.Serie);
        Assert.Equal(temporada, c.Temporada);
        Assert.Equal(episodio, c.Episodio);
        Assert.Equal($"S{temporada:00}E{episodio:00}", c.Rotulo);
    }

    [Fact]
    public void Attack_on_Titan_agrupa_com_Ataque_dos_Titas()
    {
        var c = MediaNomeParser.Classificar(
            "Series/Attack on Titan/Attack.on.Titan.S03.1080p.CR.WEB-DL.AAC2.0.H.264.DUAL/Attack.on.Titan.S03E01.1080p.CR.WEB-DL.AAC2.0.H.264.DUAL.mkv", "x");

        Assert.Equal("ataque dos titas", c.SerieChave);
    }

    [Fact]
    public void Temporada_inteira_sem_marcador_de_episodio_nao_vira_episodio()
    {
        // o nome da PASTA da T3 de Attack on Titan, como arquivo solto: não há episódio nenhum nele
        var c = MediaNomeParser.Classificar("Attack.on.Titan.S03.1080p.CR.WEB-DL.AAC2.0.H.264.DUAL.mkv", "x");

        Assert.False(c.EhEpisodio);
    }

    [Theory]
    [InlineData("DDUVT08EP13.mp4")]
    [InlineData("T V D/DDUVT08EP13.mp4")]
    [InlineData("The Vampire Diaries/DDUVT08EP13.mp4")]
    public void DDUVT08EP13_junta_com_Diarios_de_Um_Vampiro(string caminho)
    {
        var c = MediaNomeParser.Classificar(caminho, "x");

        Assert.True(c.EhEpisodio);
        Assert.Equal((8, 13), (c.Temporada, c.Episodio));
        Assert.Equal("diarios de um vampiro", c.SerieChave);
    }
}
