using FilmesApi.Services;

namespace FilmesApi.Tests;

public class CapituloDeAberturaTests
{
    [Theory]
    [InlineData("Opening", CapituloDeAbertura.Tipo.Abertura)]
    [InlineData("OPENING", CapituloDeAbertura.Tipo.Abertura)]
    [InlineData("Intro", CapituloDeAbertura.Tipo.Abertura)]
    [InlineData("Abertura", CapituloDeAbertura.Tipo.Abertura)]
    [InlineData("OP", CapituloDeAbertura.Tipo.Abertura)]
    [InlineData("Opening 1", CapituloDeAbertura.Tipo.Abertura)]
    [InlineData("Credits", CapituloDeAbertura.Tipo.Creditos)]
    [InlineData("Créditos", CapituloDeAbertura.Tipo.Creditos)]
    [InlineData("Creditos", CapituloDeAbertura.Tipo.Creditos)]
    [InlineData("Encerramento", CapituloDeAbertura.Tipo.Creditos)]
    [InlineData("Ending", CapituloDeAbertura.Tipo.Creditos)]
    [InlineData("ED", CapituloDeAbertura.Tipo.Creditos)]
    [InlineData("Prologue", CapituloDeAbertura.Tipo.Outro)]
    [InlineData("Episode", CapituloDeAbertura.Tipo.Outro)]
    [InlineData("Operação Final", CapituloDeAbertura.Tipo.Outro)]   // começa com "op" mas não é "OP"
    [InlineData(null, CapituloDeAbertura.Tipo.Outro)]
    public void Titulo_vira_tipo(string? titulo, CapituloDeAbertura.Tipo esperado) =>
        Assert.Equal(esperado, CapituloDeAbertura.Classificar(titulo));

    [Fact]
    public void Abertura_do_arquivo_real_de_Attack_on_Titan_e_a_do_capitulo()
    {
        var caps = new CapituloArquivo[] { new(0, 206.65, "Prologue"), new(206.65, 298.13, "Opening"), new(298.13, 1300, "Episode"), new(1300, 1420, "Credits") };
        Assert.Equal((206.65, 298.13), CapituloDeAbertura.Abertura(caps));
        Assert.Equal(1300, CapituloDeAbertura.InicioDosCreditos(caps));
    }

    [Fact]
    public void Opening_de_2_3_segundos_nao_e_abertura()
    {
        var caps = new CapituloArquivo[] { new(0, 100, "Episode"), new(100, 102.3, "Opening") };
        Assert.Null(CapituloDeAbertura.Abertura(caps));
    }

    [Fact]
    public void Sem_capitulos_ou_so_Credits_nao_ha_abertura()
    {
        Assert.Null(CapituloDeAbertura.Abertura(null));
        Assert.Null(CapituloDeAbertura.Abertura([new(1300, 1420, "Credits")]));
        Assert.Null(CapituloDeAbertura.InicioDosCreditos([new(0, 90, "Opening")]));
    }

    [Fact]
    public void Ffprobe_com_capitulos_vira_MediaInfo_com_capitulos()
    {
        var json = """{"streams":[],"format":{"duration":"1420.0"},"chapters":[{"start_time":"0.000000","end_time":"206.650000","tags":{"title":"Prologue"}},{"start_time":"206.65","end_time":"298.13","tags":{"title":"Opening"}}]}""";
        var info = MediaProbeService.Parsear(json);
        Assert.Equal(2, info.Capitulos!.Count);
        Assert.Equal(new CapituloArquivo(206.65, 298.13, "Opening"), info.Capitulos[1]);
        Assert.Empty(MediaProbeService.Parsear("""{"streams":[],"format":{}}""").Capitulos!);   // sem a chave: lista vazia
    }
}
