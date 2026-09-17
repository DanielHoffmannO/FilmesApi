using FilmesApi.Models;
using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// <see cref="FilmeService.MontarTela"/> é o que centralizou no backend o agrupamento que
/// antes vivia (triplicado, e com bugs) em index.html/controle.html/tv.html — filtro, contagem
/// de pasta, "1 filme real + extras" e ordenação de série tudo aqui, testável sem banco.
/// </summary>
public class MontarTelaTests
{
    private static FilmeResponse F(
        int id, string titulo, string pasta = "Sem pasta", bool assistido = false,
        bool ehEpisodio = false, bool ehExtra = false, string? serie = null, string? serieChave = null,
        int? temporada = null, int? episodio = null) =>
        new(id, titulo, null, null, assistido, DateTime.UtcNow, null, null,
            EhEpisodio: ehEpisodio, EhExtra: ehExtra, Serie: serie, SerieChave: serieChave,
            Temporada: temporada, Episodio: episodio, Rotulo: "", Pasta: pasta);

    [Fact]
    public void Filme_sem_pasta_vira_filme_solto()
    {
        var todos = new List<FilmeResponse> { F(1, "Interestelar") };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Single(tela.FilmesSoltos);
        Assert.Empty(tela.PastasFilme);
        Assert.Empty(tela.Series);
    }

    [Fact]
    public void Pasta_com_2_filmes_reais_vira_pasta_agrupada()
    {
        // 2 arquivos "de verdade" (nenhum extra) na mesma pasta -- ex.: 2 versões/cortes do
        // mesmo filme. Só "1 real + resto extra" que colapsa pra filme solto (ver próximo teste).
        var todos = new List<FilmeResponse>
        {
            F(1, "Filme Versao Cinema", pasta: "Filme (2020)"),
            F(2, "Filme Versao Estendida", pasta: "Filme (2020)"),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Empty(tela.FilmesSoltos);
        var grupo = Assert.Single(tela.PastasFilme);
        Assert.Equal("Filme (2020)", grupo.Pasta);
        Assert.Equal(2, grupo.Itens.Count);
    }

    [Fact]
    public void Pasta_com_1_filme_real_mais_extras_achata_pro_filme_solto()
    {
        var todos = new List<FilmeResponse>
        {
            F(1, "Filme", pasta: "Filme (2020)"),
            F(2, "Filme sample", pasta: "Filme (2020)", ehExtra: true),
            F(3, "Filme trailer", pasta: "Filme (2020)", ehExtra: true),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Empty(tela.PastasFilme);
        var solto = Assert.Single(tela.FilmesSoltos);
        Assert.Equal(1, solto.Id);
    }

    [Fact]
    public void Episodios_agrupam_por_serieChave_mesmo_com_nomes_de_exibicao_diferentes()
    {
        var todos = new List<FilmeResponse>
        {
            F(1, "1 - Pilot", ehEpisodio: true, serie: "Diarios de Um Vampiro", serieChave: "diarios de um vampiro", temporada: 1, episodio: 1),
            F(2, "2 - Next", ehEpisodio: true, serie: "Diários de um Vampiro", serieChave: "diarios de um vampiro", temporada: 2, episodio: 1),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        var grupo = Assert.Single(tela.Series);
        Assert.Equal("diarios de um vampiro", grupo.Chave);
        Assert.Equal(2, grupo.Episodios.Count);
        // ordenado por temporada/episodio
        Assert.Equal(1, grupo.Episodios[0].Id);
        Assert.Equal(2, grupo.Episodios[1].Id);
    }

    [Fact]
    public void Filtro_tipo_filme_exclui_episodios()
    {
        var todos = new List<FilmeResponse>
        {
            F(1, "Filme"),
            F(2, "Ep", ehEpisodio: true, serie: "Show", serieChave: "show"),
        };
        var tela = FilmeService.MontarTela(todos, [], "filme", "all", null);

        Assert.Single(tela.FilmesSoltos);
        Assert.Empty(tela.Series);
    }

    [Fact]
    public void Filtro_tipo_serie_exclui_filmes()
    {
        var todos = new List<FilmeResponse>
        {
            F(1, "Filme"),
            F(2, "Ep", ehEpisodio: true, serie: "Show", serieChave: "show"),
        };
        var tela = FilmeService.MontarTela(todos, [], "serie", "all", null);

        Assert.Empty(tela.FilmesSoltos);
        Assert.Single(tela.Series);
    }

    [Theory]
    [InlineData("assistido", 1)]
    [InlineData("nao-assistido", 1)]
    [InlineData("all", 2)]
    public void Filtro_visto(string visto, int esperado)
    {
        var todos = new List<FilmeResponse> { F(1, "A", assistido: true), F(2, "B", assistido: false) };
        var tela = FilmeService.MontarTela(todos, [], "all", visto, null);

        Assert.Equal(esperado, tela.FilmesSoltos.Count);
    }

    [Fact]
    public void Busca_filtra_por_titulo_serie_ou_pasta_sem_diferenciar_maiuscula()
    {
        var todos = new List<FilmeResponse>
        {
            F(1, "Interestelar"),
            F(2, "Outro Filme"),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", "INTERESTELAR");

        Assert.Single(tela.FilmesSoltos);
        Assert.Equal(1, tela.FilmesSoltos[0].Id);
    }

    [Fact]
    public void ContinuarAssistindo_so_aparece_com_tipo_e_visto_neutros()
    {
        var candidatos = new List<FilmeResponse> { F(1, "Em andamento") };

        var comFiltroNeutro = FilmeService.MontarTela([], candidatos, "all", "all", null);
        Assert.Single(comFiltroNeutro.ContinuarAssistindo);

        var comFiltroTipo = FilmeService.MontarTela([], candidatos, "filme", "all", null);
        Assert.Empty(comFiltroTipo.ContinuarAssistindo);

        var comFiltroVisto = FilmeService.MontarTela([], candidatos, "all", "assistido", null);
        Assert.Empty(comFiltroVisto.ContinuarAssistindo);
    }

    [Fact]
    public void ContinuarAssistindo_tambem_respeita_a_busca()
    {
        var candidatos = new List<FilmeResponse> { F(1, "Interestelar"), F(2, "Outro") };
        var tela = FilmeService.MontarTela([], candidatos, "all", "all", "interestelar");

        Assert.Single(tela.ContinuarAssistindo);
        Assert.Equal(1, tela.ContinuarAssistindo[0].Id);
    }

    // Regressão: sem CatalogoVazio, as 3 telas faziam um GET /api/filmes à parte (classificando
    // o catálogo inteiro de novo) só pra saber se tinha ALGUMA coisa, além do GET /tela que já
    // classifica tudo mesmo. CatalogoVazio some com essa segunda chamada.
    [Fact]
    public void CatalogoVazio_reflete_a_lista_completa_nao_a_filtrada()
    {
        var todos = new List<FilmeResponse> { F(1, "Interestelar") };

        Assert.True(FilmeService.MontarTela([], [], "all", "all", null).CatalogoVazio);
        // Filtro que não bate nada: catálogo tem filme, só não passou no filtro atual.
        Assert.False(FilmeService.MontarTela(todos, [], "all", "all", "nao existe").CatalogoVazio);
        Assert.False(FilmeService.MontarTela(todos, [], "serie", "all", null).CatalogoVazio);
    }

    [Fact]
    public void Contagem_de_pasta_pro_limiar_de_agrupar_usa_a_lista_inteira()
    {
        // porPasta é calculado sobre TODOS os arquivos, não só os que passaram na busca --
        // senão uma busca que esconde 1 de 2 arquivos reais faria a pasta "achatar" sozinha
        // (limiar de agrupar cairia de 2 pra 1) mesmo com os 2 arquivos reais ainda existindo.
        var todos = new List<FilmeResponse>
        {
            F(1, "Filme Principal", pasta: "Filme (2020)"),
            F(2, "Filme Trailer", pasta: "Filme (2020)", ehExtra: true),
        };
        // busca só bate o trailer -- o principal (não-extra) fica de fora do filtrado
        var tela = FilmeService.MontarTela(todos, [], "all", "all", "trailer");

        // porPasta (da lista inteira) = 2 > 1 -> ainda tenta agrupar como pasta, não filme solto
        var grupo = Assert.Single(tela.PastasFilme);
        Assert.Single(grupo.Itens);
        Assert.Equal(2, grupo.Itens[0].Id);
    }
}
