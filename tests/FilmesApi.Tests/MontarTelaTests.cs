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
        Assert.Equal("Filme (2020)", grupo.Chave);
        Assert.Equal("Filme 2020", grupo.Nome);  // NomePastaExibicao tira os parênteses
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

    // ─── Franquia de filme por título (Homem-Aranha 1 fora da "TRILOGIA", Toy Story) ────

    [Fact]
    public void Franquia_agrupa_filmes_soltos_cada_um_na_sua_propria_pasta()
    {
        // Toy Story real: cada filme mora sozinho na sua própria pasta -- sem pasta-mãe
        // "Trilogia .../" nenhuma ligando eles.
        var todos = new List<FilmeResponse>
        {
            F(1, "Toy Story", pasta: "Toy Story (1995)"),
            F(2, "Toy Story 2", pasta: "Toy Story 2 (1999)"),
            F(3, "Toy Story 3", pasta: "Toy Story 3 (2010)"),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Empty(tela.FilmesSoltos);
        var grupo = Assert.Single(tela.PastasFilme);
        Assert.Equal("Toy Story", grupo.Nome);
        Assert.Equal(3, grupo.Itens.Count);
    }

    [Fact]
    public void Franquia_junta_filme_solto_com_pasta_de_verdade_da_mesma_franquia()
    {
        // Caso real: Homem-Aranha 1 sozinho na própria pasta; 2 e 3 JÁ agrupados numa pasta
        // de release ("TRILOGIA...", 2 arquivos reais). O filme solto tem que entrar no MESMO
        // grupo da pasta de verdade, não formar um terceiro grupo à parte.
        var todos = new List<FilmeResponse>
        {
            F(1, "Homem Aranha", pasta: "Homem-Aranha (2002)"),
            F(2, "Homem Aranha 2", pasta: "TRILOGIA Homem-Aranha"),
            F(3, "Homem Aranha 3", pasta: "TRILOGIA Homem-Aranha"),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Empty(tela.FilmesSoltos);
        var grupo = Assert.Single(tela.PastasFilme);
        Assert.Equal("Homem Aranha", grupo.Nome);
        Assert.Equal(3, grupo.Itens.Count);
        Assert.Equal([1, 2, 3], grupo.Itens.Select(i => i.Id).OrderBy(i => i));
    }

    [Fact]
    public void Franquia_nao_agrupa_titulos_parecidos_sem_marcador_de_sequencia()
    {
        // "Poder" e "Poder Absoluto" não têm relação nenhuma -- não podem virar uma
        // "franquia" só por compartilhar a primeira palavra.
        var todos = new List<FilmeResponse>
        {
            F(1, "Poder", pasta: "Poder (2010)"),
            F(2, "Poder Absoluto", pasta: "Poder Absoluto (2015)"),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Empty(tela.PastasFilme);
        Assert.Equal(2, tela.FilmesSoltos.Count);
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

    // Regressão real: um grupo mesclado (via SerieChave/apelido) tinha nomes "brutos"
    // diferentes por temporada — "Diários de Um Vampiro" em 127 episódios contra "T V D S07
    // WWW TORRENTDOSFILMES COM" em só 22. Escolher o PRIMEIRO nome visto (ordem de
    // DataAdicionado) podia fazer o nome feio da release vencer a exibição só por coincidência
    // de qual arquivo foi adicionado mais recentemente ao catálogo.
    [Fact]
    public void Nome_de_exibicao_da_serie_e_o_mais_frequente_nao_o_primeiro_visto()
    {
        var todos = new List<FilmeResponse>();
        // 3 episódios com o nome feio de release, adicionados ANTES (Id menor = mais antigo
        // na ordem de inserção da lista, mas isso não deveria importar de qualquer forma).
        for (var i = 1; i <= 3; i++)
            todos.Add(F(i, $"{i} - x", ehEpisodio: true,
                serie: "T V D S07 WWW TORRENTDOSFILMES COM", serieChave: "diarios de um vampiro", temporada: 7, episodio: i));
        // 10 episódios com o nome "de verdade" -- deve vencer por frequência, mesmo entrando depois.
        for (var i = 4; i <= 13; i++)
            todos.Add(F(i, $"{i} - x", ehEpisodio: true,
                serie: "Diários de Um Vampiro", serieChave: "diarios de um vampiro", temporada: 1, episodio: i));

        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        var grupo = Assert.Single(tela.Series);
        Assert.Equal("Diários de Um Vampiro", grupo.Nome);
        Assert.Equal(13, grupo.Episodios.Count);
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
        // senão uma busca que esconde 1 dos 3 arquivos reais faria a pasta "achatar" sozinha
        // (limiar de agrupar cairia) mesmo com os 3 arquivos reais ainda existindo.
        var todos = new List<FilmeResponse>
        {
            F(1, "Filme Parte Um", pasta: "Filme (2020)"),
            F(2, "Filme Parte Dois", pasta: "Filme (2020)"),
            F(3, "Filme Trailer", pasta: "Filme (2020)", ehExtra: true),
        };
        // busca bate as 2 partes reais, não bate o trailer
        var tela = FilmeService.MontarTela(todos, [], "all", "all", "parte");

        var grupo = Assert.Single(tela.PastasFilme);
        Assert.Equal(2, grupo.Itens.Count);
    }

    // Regressão real: pasta "COMANDO.TO - Contos do Loop..." tinha 8 arquivos -- 7 viraram
    // episódio de série (saem do caminho de agrupar filme totalmente) e sobrou 1 só, um vídeo
    // de propaganda do uploader (EhExtra=true). Sem essa checagem, esse arquivo sozinho
    // formava uma "pasta de filme" com puro lixo dentro, sem filme real nenhum pra mostrar.
    [Fact]
    public void Pasta_onde_sobrou_so_extra_e_descartada_inteira()
    {
        var todos = new List<FilmeResponse>
        {
            F(1, "Ep1", pasta: "Serie Pasta", ehEpisodio: true, serie: "Serie", serieChave: "serie"),
            F(2, "1XBET.COM promo", pasta: "Serie Pasta", ehExtra: true),
        };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Empty(tela.PastasFilme);
        Assert.Empty(tela.FilmesSoltos);
        Assert.Single(tela.Series);
    }

    // Regressão: um trailer/sample/propaganda sozinho (sem pasta, ou pasta que nunca bateu o
    // limiar de 2+) não tem filme nenhum pra "pertencer" -- não devia aparecer na lista como
    // se fosse um filme de verdade.
    [Fact]
    public void Extra_solto_sem_pasta_nao_aparece_como_filme()
    {
        var todos = new List<FilmeResponse> { F(1, "Sample", ehExtra: true) };
        var tela = FilmeService.MontarTela(todos, [], "all", "all", null);

        Assert.Empty(tela.FilmesSoltos);
        Assert.Empty(tela.PastasFilme);
    }
}
