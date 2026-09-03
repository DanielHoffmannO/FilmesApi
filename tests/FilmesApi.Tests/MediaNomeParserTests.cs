using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Corpus do <see cref="MediaNomeParser"/> — a peça que mais quebra com nome de arquivo do
/// mundo real. Casos reais (Breaking Bad em 3 layouts diferentes) + armadilhas conhecidas
/// (ano no título, resolução parecendo NxNN, pasta de filmes numerados).
/// </summary>
public class MediaNomeParserTests
{
    // ─── OrdemEpisodio: o que é episódio e em que ordem ──────────────────

    [Theory]
    // SxxExx no nome
    [InlineData("Better Call Saul/BCS.S01E03.1080p.mkv", 1, 3)]
    [InlineData("Show S02E10.mkv", 2, 10)]
    [InlineData("algo/Show.s3e7.mkv", 3, 7)]
    // NxNN no nome
    [InlineData("Serie/Serie 1x02 piloto.mkv", 1, 2)]
    [InlineData("Serie/3x14 - final.mkv", 3, 14)]
    // "Episódio N"
    [InlineData("Anime/Episódio 5.mkv", 0, 5)]
    [InlineData("Anime/Capitulo 12 - x.mkv", 0, 12)]
    // número solto no nome + temporada NA PASTA (caso Breaking Bad "The Pirate Filmes")
    [InlineData("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", 3, 8)]
    [InlineData("Breaking Bad 5 Temporada Parte 2 -  The Pirate Filmes/14 - Ozymandias.mp4", 5, 14)]
    [InlineData("Show/2ª Temporada/07. Titulo.mkv", 2, 7)]
    [InlineData("Show/Season 4/[12] Titulo.mkv", 4, 12)]
    // SxxExx ganha da temporada da pasta
    [InlineData("Breaking Bad 2011 4ª Temporada Completa [WWW.BLUDV.COM]/Breaking.Bad.2011.S04E09.720p.BluRay.x264.DUAL.mkv", 4, 9)]
    public void OrdemEpisodio_reconhece(string path, int temp, int ep)
    {
        Assert.Equal((temp, ep), MediaNomeParser.OrdemEpisodio(path));
    }

    [Theory]
    [InlineData("Filmes/Interestelar 2014 1080p Dublado.mkv")]        // filme solto
    [InlineData("Colecao Rocky/1 - Rocky (1976).mp4")]                 // pasta de filmes numerados, SEM token de temporada
    [InlineData("Colecao Rocky/2 - Rocky II (1979).mp4")]
    [InlineData("Filmes/Blade Runner 2049 (2017) 2160p.mkv")]         // "2049" não é NxNN
    [InlineData("Filmes/1917 (2019).mkv")]                            // número puro no começo, sem separador
    [InlineData("Filmes/2001 A Space Odyssey.mkv")]
    [InlineData("Documentarios/Cosmos 1980 1x01.mkv")]                // tem NxNN mas... (ver nota no teste)
    public void OrdemEpisodio_nao_confunde_filme_com_episodio(string path)
    {
        // "Cosmos 1980 1x01" REALMENTE casa NxNN — é o preço de aceitar "1x01".
        // Mantido aqui só como lembrete; se um dia o parser ficar mais esperto, trocar p/ Null.
        if (path.Contains("1x01")) { Assert.NotNull(MediaNomeParser.OrdemEpisodio(path)); return; }
        Assert.Null(MediaNomeParser.OrdemEpisodio(path));
    }

    [Theory]
    [InlineData("Serie/Show.720p.x264.mkv")]      // 720p / x264 não são NxNN
    [InlineData("Serie/Show 1920x1080.mkv")]      // resolução não é NxNN
    public void OrdemEpisodio_ignora_resolucao_e_codec(string path)
    {
        Assert.Null(MediaNomeParser.OrdemEpisodio(path));
    }

    // ─── ChaveSerie: nome pra agrupar ───────────────────────────────────

    [Theory]
    [InlineData("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", "Breaking Bad")]
    [InlineData("Breaking Bad 5 Temporada Parte 2 -  The Pirate Filmes/14 - Ozymandias.mp4", "Breaking Bad")]
    [InlineData("Breaking Bad 2011 4ª Temporada Completa [WWW.BLUDV.COM]/Breaking.Bad.2011.S04E09.mkv", "Breaking Bad")]
    [InlineData("Better Call Saul/Season 1/BCS.S01E03.mkv", "Better Call Saul")]
    [InlineData("The Office (US)/S03/The.Office.S03E02.mkv", "The Office (US)")]
    [InlineData("Show.S01E01.mkv", "Show")]
    public void ChaveSerie(string path, string esperado)
    {
        Assert.Equal(esperado, MediaNomeParser.ChaveSerie(path));
    }

    [Fact]
    public void ChaveSerie_agrupa_temporadas_em_subpastas()
    {
        var s1 = MediaNomeParser.ChaveSerie("Breaking Bad/Season 1/BB.S01E07.mkv");
        var s2 = MediaNomeParser.ChaveSerie("Breaking Bad/Season 2/BB.S02E01.mkv");
        Assert.Equal(s1, s2);
        Assert.Equal("Breaking Bad", s1);
    }

    // ─── Classificar: o pacote que vai pro FilmeResponse ────────────────

    [Fact]
    public void Classificar_filme()
    {
        var c = MediaNomeParser.Classificar("Filmes/Interestelar 2014 1080p.mkv", "Interestelar");
        Assert.False(c.EhEpisodio);
        Assert.False(c.EhExtra);
        Assert.Null(c.Serie);
        Assert.Equal("Interestelar", c.Rotulo);   // filme: rótulo = título passado
        Assert.Equal("Filmes", c.Pasta);
    }

    [Fact]
    public void Classificar_episodio_com_temporada_na_pasta()
    {
        var c = MediaNomeParser.Classificar("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", "8 - I See You");
        Assert.True(c.EhEpisodio);
        Assert.Equal("Breaking Bad", c.Serie);
        Assert.Equal(3, c.Temporada);
        Assert.Equal(8, c.Episodio);
        Assert.Equal("S03E08", c.Rotulo);
    }

    [Theory]
    [InlineData("Filme (2020)/trailer.mkv")]
    [InlineData("Filme (2020)/Filme.2020.sample.mkv")]
    [InlineData("Serie/S01/promo 1xbet.mkv")]
    public void Classificar_marca_extra(string path)
    {
        Assert.True(MediaNomeParser.Classificar(path, "x").EhExtra);
    }

    [Fact]
    public void Classificar_episodio_sem_temporada_monta_rotulo_com_titulo()
    {
        var c = MediaNomeParser.Classificar("Anime/Episódio 12 - O Confronto.mkv", "Episódio 12 - O Confronto");
        Assert.True(c.EhEpisodio);
        Assert.Equal(0, c.Temporada);
        Assert.Equal(12, c.Episodio);
        Assert.Equal("Ep 12 · O Confronto", c.Rotulo);
    }

    // ─── TituloParaBusca: nome limpo pro TMDB ───────────────────────────

    [Theory]
    [InlineData("Filmes/Blade Runner 2049 2017 1080p BluRay x264.mkv", "Blade Runner 2049", 2017)]
    [InlineData("Filmes/Interestelar.2014.1080p.Dublado.mkv", "Interestelar", 2014)]
    [InlineData("Filmes/O Poderoso Chefao (1972).mkv", "O Poderoso Chefao", 1972)]
    [InlineData("Show/Show.S02E05.720p.WEB-DL.mkv", "Show", null)]
    [InlineData("Breaking Bad 2011 4ª Temporada Completa [WWW.BLUDV.COM]/Breaking.Bad.2011.S04E09.720p.BluRay.x264.DUAL.mkv", "Breaking Bad", 2011)]
    // episódio "N - Título": o número vira ruído (já está em Temporada/Episódio)
    [InlineData("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", "I See You", null)]
    [InlineData("Show/Season 2/07. O Retorno.mkv", "O Retorno", null)]
    public void TituloParaBusca(string path, string titulo, int? ano)
    {
        var r = MediaNomeParser.TituloParaBusca(path);
        Assert.Equal(titulo, r.Titulo);
        Assert.Equal(ano, r.Ano);
    }

    [Fact]
    public void TituloParaBusca_episodio_numerado_cai_pro_nome_da_serie()
    {
        // "8 - I See You" sozinho não serve de busca; usa a série da pasta.
        var r = MediaNomeParser.TituloParaBusca("Breaking Bad 3 Temporada - The Pirate Filmes/8.mp4");
        Assert.Equal("Breaking Bad", r.Titulo);
    }
}
