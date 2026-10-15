using FilmesApi.Models;
using FilmesApi.Services;

namespace FilmesApi.Tests;

[Collection("NomesGlobais")]   // conta recálculos: não pode rodar junto de quem troca o nomes.json (versão global)
public class ClassificacaoCacheTests
{
    private static FilmeResponse Filme(int id, string? caminho, string titulo = "T", bool assistido = false, double? pos = null) =>
        new(id, titulo, null, caminho, assistido, DateTime.UtcNow, pos, null, null, null, null,
            false, false, null, null, null, null, "", "Sem pasta");

    private static List<FilmeResponse> Catalogo() =>
    [
        Filme(1, "Series/Show/Show.S01E01.mkv"),
        Filme(2, "Series/Show/Show.S01E02.mkv"),
        Filme(3, "Filmes/Matrix (1999)/Matrix.1999.mkv", "Matrix"),
    ];

    [Fact]
    public void Resultado_e_igual_a_classificar_direto()
    {
        var lista = Catalogo();
        var contagem = MediaNomeParser.ContarNumeradosPorPasta(lista.Select(f => f.ArquivoPath));

        var viaCache = new ClassificacaoCache().Classificar(lista);

        for (var i = 0; i < lista.Count; i++)
        {
            var esperado = MediaNomeParser.Classificar(lista[i].ArquivoPath, lista[i].Titulo, contagem);
            Assert.Equal(esperado.EhEpisodio, viaCache[i].EhEpisodio);
            Assert.Equal(esperado.EhExtra, viaCache[i].EhExtra);
            Assert.Equal(esperado.Serie, viaCache[i].Serie);
            Assert.Equal(esperado.SerieChave, viaCache[i].SerieChave);
            Assert.Equal(esperado.Temporada, viaCache[i].Temporada);
            Assert.Equal(esperado.Episodio, viaCache[i].Episodio);
            Assert.Equal(esperado.Rotulo, viaCache[i].Rotulo);
            Assert.Equal(esperado.Pasta, viaCache[i].Pasta);
        }
    }

    [Fact]
    public void Lista_igual_na_chamada_seguinte_nao_reclassifica()
    {
        var cache = new ClassificacaoCache();
        var a = cache.Classificar(Catalogo());
        var b = cache.Classificar(Catalogo());

        Assert.Equal(1, cache.Recalculos);
        Assert.Equal(a.Select(f => (f.Id, f.Rotulo, f.Temporada, f.Episodio)), b.Select(f => (f.Id, f.Rotulo, f.Temporada, f.Episodio)));
    }

    [Fact]
    public void Progresso_e_assistido_vem_sempre_frescos_sem_reclassificar()
    {
        var cache = new ClassificacaoCache();
        cache.Classificar(Catalogo());

        var mudou = Catalogo();
        mudou[0] = Filme(1, mudou[0].ArquivoPath, mudou[0].Titulo, assistido: true, pos: 321);
        var r = cache.Classificar(mudou);

        Assert.Equal(1, cache.Recalculos);
        Assert.True(r[0].Assistido);
        Assert.Equal(321, r[0].PosicaoSegundos);
        Assert.True(r[0].EhEpisodio);   // a classificação guardada foi aplicada por cima
    }

    [Fact]
    public void Titulo_diferente_reclassifica()
    {
        var cache = new ClassificacaoCache();
        cache.Classificar(Catalogo());

        var mudou = Catalogo();
        mudou[2] = Filme(3, mudou[2].ArquivoPath, "Matrix Reloaded");
        cache.Classificar(mudou);

        Assert.Equal(2, cache.Recalculos);
    }

    [Fact]
    public void Arquivo_novo_ou_removido_reclassifica()
    {
        var cache = new ClassificacaoCache();
        cache.Classificar(Catalogo());

        var comNovo = Catalogo();
        comNovo.Add(Filme(4, "Series/Show/Show.S01E03.mkv"));
        cache.Classificar(comNovo);
        cache.Classificar(Catalogo());   // voltou ao tamanho antigo: o guardado era o de 4, recalcula de novo

        Assert.Equal(3, cache.Recalculos);
    }

    [Fact]
    public void Caminho_diferente_reclassifica_e_reflete_na_saida()
    {
        var cache = new ClassificacaoCache();
        cache.Classificar([Filme(1, "Filmes/X/X.mkv")]);

        var r = cache.Classificar([Filme(1, "Series/Y/Y.S02E05.mkv")]);

        Assert.Equal(2, cache.Recalculos);
        Assert.True(r[0].EhEpisodio);
        Assert.Equal(2, r[0].Temporada);
        Assert.Equal(5, r[0].Episodio);
    }

    [Fact]
    public void Lista_vazia_funciona()
    {
        var cache = new ClassificacaoCache();
        Assert.Empty(cache.Classificar([]));
        Assert.Empty(cache.Classificar([]));
        Assert.Equal(1, cache.Recalculos);
    }
}
