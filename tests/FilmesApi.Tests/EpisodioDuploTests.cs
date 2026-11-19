using FilmesApi.Services;

namespace FilmesApi.Tests;

public class EpisodioDuploTests
{
    [Fact]
    public void Episodio_duplo_11e_12_da_antologia_entra_na_serie_como_o_11()
    {
        const string pasta = "extra/Alem da Imaginação";
        var caminhos = Enumerable.Range(1, 40).Select(n => $"{pasta}/{n:00} Fusao Fria {n} .mp4").ToList();
        var duplo = $"{pasta}/11e 12 O Reparador de Linhas Telefônicas Parte 1e Parte 2 .mp4";
        caminhos.Add(duplo);
        var c = MediaNomeParser.Classificar(duplo, "t", MediaNomeParser.ContarNumeradosPorPasta(caminhos));
        Assert.True(c.EhEpisodio);
        Assert.Equal(11, c.Episodio);
    }
}
