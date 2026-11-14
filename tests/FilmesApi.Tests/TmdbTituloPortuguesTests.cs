using System.Text.Json;
using FilmesApi.Services;

namespace FilmesApi.Tests;

public class TmdbTituloPortuguesTests
{
    [Theory]
    [InlineData("銀河鉄道999 エターナルファンタジー", true)]
    [InlineData("Галактика", true)]
    [InlineData("은하철도", true)]
    [InlineData("Ação Máxima: O Retorno", false)]
    [InlineData("Việt Nam", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detecta_escrita_nao_latina(string? texto, bool esperado) =>
        Assert.Equal(esperado, TmdbService.TemEscritaNaoLatina(texto));

    private static JsonDocument Traducoes(string json) => JsonDocument.Parse(json);

    [Fact]
    public void Prefere_pt_BR_depois_pt_PT_depois_ingles()
    {
        var doc = Traducoes("""
        {"translations":[
          {"iso_639_1":"en","iso_3166_1":"US","data":{"title":"Galaxy Express 999"}},
          {"iso_639_1":"pt","iso_3166_1":"PT","data":{"title":"Galáxia Expresso 999 (PT)"}},
          {"iso_639_1":"pt","iso_3166_1":"BR","data":{"title":"Galaxy Express 999: Fantasia Eterna"}}]}
        """);
        Assert.Equal("Galaxy Express 999: Fantasia Eterna", TmdbService.EscolherTituloLatino(doc, serie: false));

        var semBr = Traducoes("""
        {"translations":[
          {"iso_639_1":"en","iso_3166_1":"US","data":{"title":"Galaxy Express 999"}},
          {"iso_639_1":"pt","iso_3166_1":"PT","data":{"title":"Galáxia Expresso 999"}}]}
        """);
        Assert.Equal("Galáxia Expresso 999", TmdbService.EscolherTituloLatino(semBr, serie: false));

        var soIngles = Traducoes("""
        {"translations":[{"iso_639_1":"pt","iso_3166_1":"BR","data":{"title":""}},
          {"iso_639_1":"en","iso_3166_1":"US","data":{"title":"Galaxy Express 999"}}]}
        """);
        Assert.Equal("Galaxy Express 999", TmdbService.EscolherTituloLatino(soIngles, serie: false));
    }

    [Fact]
    public void Serie_usa_o_campo_name_e_ignora_traducao_ainda_nao_latina()
    {
        var doc = Traducoes("""
        {"translations":[
          {"iso_639_1":"pt","iso_3166_1":"BR","data":{"name":"鉄道"}},
          {"iso_639_1":"en","iso_3166_1":"US","data":{"name":"Railway"}}]}
        """);
        Assert.Equal("Railway", TmdbService.EscolherTituloLatino(doc, serie: true));
        Assert.Null(TmdbService.EscolherTituloLatino(Traducoes("""{"translations":[]}"""), serie: false));
    }
}
