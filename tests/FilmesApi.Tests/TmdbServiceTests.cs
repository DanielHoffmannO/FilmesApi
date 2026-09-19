using System.Text.Json;
using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// InterpretarResposta é a extração pura (sem rede) do JSON de busca do TMDB — testa contra
/// respostas de exemplo em vez de bater no TMDB de verdade.
/// </summary>
public class TmdbServiceTests
{
    private const string ImgBase = "https://image.tmdb.org/t/p/w342";

    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    [Fact]
    public void Sem_resultados_devolve_null()
    {
        var r = TmdbService.InterpretarResposta(Doc("""{"results":[]}"""), serie: false, ImgBase);
        Assert.Null(r);
    }

    [Fact]
    public void Sem_a_propriedade_results_devolve_null()
    {
        var r = TmdbService.InterpretarResposta(Doc("""{"algo_inesperado":1}"""), serie: false, ImgBase);
        Assert.Null(r);
    }

    [Fact]
    public void Resultado_sem_id_devolve_null()
    {
        var r = TmdbService.InterpretarResposta(
            Doc("""{"results":[{"title":"Sem Id"}]}"""), serie: false, ImgBase);
        Assert.Null(r);
    }

    [Fact]
    public void Id_zero_devolve_null()
    {
        // TMDB nunca deveria devolver id 0 de verdade, mas 0 é o default de int em caso de
        // campo ausente/malformado -- tratar como "sem resultado utilizável", não como id real.
        var r = TmdbService.InterpretarResposta(
            Doc("""{"results":[{"id":0,"title":"X"}]}"""), serie: false, ImgBase);
        Assert.Null(r);
    }

    [Fact]
    public void Filme_usa_title_e_original_title()
    {
        var r = TmdbService.InterpretarResposta(Doc("""
            {"results":[{"id":27205,"title":"A Origem","original_title":"Inception",
              "overview":"Um ladrão de sonhos.","poster_path":"/abc.jpg"}]}
            """), serie: false, ImgBase);

        Assert.NotNull(r);
        Assert.Equal(27205, r!.TmdbId);
        Assert.Equal("A Origem", r.Titulo);
        Assert.Equal("Inception", r.TituloOriginal);
        Assert.Equal("Um ladrão de sonhos.", r.Sinopse);
        Assert.Equal(ImgBase + "/abc.jpg", r.PosterUrl);
    }

    [Fact]
    public void Serie_usa_name_e_original_name_em_vez_de_title()
    {
        var r = TmdbService.InterpretarResposta(Doc("""
            {"results":[{"id":1396,"name":"Breaking Bad","original_name":"Breaking Bad",
              "title":"nome de filme que nao devia ser usado aqui"}]}
            """), serie: true, ImgBase);

        Assert.NotNull(r);
        Assert.Equal("Breaking Bad", r!.Titulo);
    }

    [Fact]
    public void So_pega_o_primeiro_resultado()
    {
        var r = TmdbService.InterpretarResposta(Doc("""
            {"results":[{"id":1,"title":"Primeiro"},{"id":2,"title":"Segundo"}]}
            """), serie: false, ImgBase);

        Assert.Equal(1, r!.TmdbId);
        Assert.Equal("Primeiro", r.Titulo);
    }

    [Fact]
    public void Titulo_em_branco_vira_null_em_vez_de_string_vazia()
    {
        var r = TmdbService.InterpretarResposta(Doc("""
            {"results":[{"id":5,"title":"   ","overview":""}]}
            """), serie: false, ImgBase);

        Assert.NotNull(r);
        Assert.Null(r!.Titulo);
        Assert.Null(r.Sinopse);
    }

    [Fact]
    public void Sem_poster_path_url_do_poster_fica_null()
    {
        var r = TmdbService.InterpretarResposta(
            Doc("""{"results":[{"id":5,"title":"X"}]}"""), serie: false, ImgBase);

        Assert.Null(r!.PosterUrl);
    }
}
