using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Os apelidos/nomes de exibição saíram do código pra um arquivo. O estado é global (o parser é
/// estático), então esta classe fica na mesma coleção do ClassificacaoCacheTests e usa chaves que nenhum outro
/// teste usa; o padrão embutido é restaurado ao fim de cada teste.</summary>
[Collection("NomesGlobais")]
public class NomesPersonalizadosTests : IDisposable
{
    private readonly string _dir = TestesComuns.CriarPastaTemp();
    private string Arquivo => Path.Combine(_dir, "nomes.json");

    public void Dispose()
    {
        MediaNomeParser.DefinirNomesPersonalizados(null);
        Directory.Delete(_dir, recursive: true);
    }

    private NomesPersonalizadosService Servico() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NomesPath"] = Arquivo }).Build(),
        NullLogger<NomesPersonalizadosService>.Instance);

    // ─── formato do arquivo ──────────────────────────────────────────────────────────

    [Fact]
    public void Le_apelidos_e_nomes()
    {
        var arq = NomesPersonalizadosService.Ler("""
            { "apelidos": { "attack on titan": "ataque dos titas" }, "nomes": { "ataque dos titas": "Ataque dos Titãs" }, "outra": 1 }
            """);

        Assert.Equal("ataque dos titas", arq.Apelidos["attack on titan"]);
        Assert.Equal("Ataque dos Titãs", arq.Nomes["ataque dos titas"]);
    }

    [Fact]
    public void Secoes_ausentes_valem_como_vazias()
    {
        var arq = NomesPersonalizadosService.Ler("{}");
        Assert.Empty(arq.Apelidos);
        Assert.Empty(arq.Nomes);
        Assert.Empty(arq.Ignorar);
        Assert.Empty(arq.Tipos);
    }

    [Theory]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("[1,2]")]
    [InlineData("""{ "apelidos": [1] }""")]
    [InlineData("""{ "apelidos": { "x": 3 } }""")]
    [InlineData("""{ "nomes": "texto" }""")]
    public void Arquivo_fora_do_formato_lanca(string json) =>
        Assert.ThrowsAny<Exception>(() => NomesPersonalizadosService.Ler(json));

    // ─── mesclagem com o padrão embutido ─────────────────────────────────────────────

    [Fact]
    public void Mesclar_soma_ao_padrao_sem_perder_nada()
    {
        var (a, n) = MediaNomeParser.Mesclar(
            new Dictionary<string, string> { ["zz serie nova"] = "zz canonica" },
            new Dictionary<string, string> { ["zz canonica"] = "ZZ Série Nova" });

        Assert.Equal("zz canonica", a["zz serie nova"]);
        Assert.Equal("diarios de um vampiro", a["dduv"]);           // padrão embutido segue valendo
        Assert.Equal("ZZ Série Nova", n["zz canonica"]);
        Assert.Equal("Família Soprano", n["the sopranos"]);
    }

    [Fact]
    public void Mesclar_arquivo_sobrescreve_o_padrao()
    {
        var (a, n) = MediaNomeParser.Mesclar(
            new Dictionary<string, string> { ["attack on titan"] = "shingeki" },
            new Dictionary<string, string> { ["the sopranos"] = "Os Sopranos" });

        Assert.Equal("shingeki", a["attack on titan"]);
        Assert.Equal("Os Sopranos", n["the sopranos"]);
    }

    [Fact]
    public void Mesclar_normaliza_acento_caixa_e_espaco_e_ignora_vazios()
    {
        var (a, n) = MediaNomeParser.Mesclar(
            new Dictionary<string, string> { ["  ZZ Série   COM Acento "] = " ZZ Canônica ", [""] = "x", ["y"] = "  " },
            new Dictionary<string, string> { ["ZZ Canônica"] = "  ZZ Bonita ", ["zz vazio"] = "   " });

        Assert.Equal("zz canonica", a["zz serie com acento"]);
        Assert.False(a.ContainsKey("y"));
        Assert.False(a.ContainsKey(""));
        Assert.Equal("ZZ Bonita", n["zz canonica"]);
        Assert.False(n.ContainsKey("zz vazio"));
    }

    [Fact]
    public void Mesclar_nao_suja_o_padrao_embutido()
    {
        MediaNomeParser.Mesclar(new Dictionary<string, string> { ["zz lixo"] = "zz outro" }, null);
        var (a, _) = MediaNomeParser.Mesclar(null, null);
        Assert.False(a.ContainsKey("zz lixo"));
    }

    // ─── o serviço (arquivo de verdade) ──────────────────────────────────────────────

    [Fact]
    public void Sem_arquivo_fica_o_padrao()
    {
        var r = Servico().Recarregar();

        Assert.Equal(NomesPersonalizadosService.Resultado.SemArquivo, r.Resultado);
        Assert.Equal("ataque dos titas", MediaNomeParser.ChaveAgrupamento("Attack on Titan"));
    }

    [Fact]
    public void Arquivo_valido_passa_a_valer_junto_com_o_padrao()
    {
        File.WriteAllText(Arquivo, """
            { "apelidos": { "ZZ Série Teste": "zz canonica teste" }, "nomes": { "zz canonica teste": "ZZ Série Teste Bonita" } }
            """);

        var r = Servico().Recarregar();

        Assert.Equal(NomesPersonalizadosService.Resultado.Aplicado, r.Resultado);
        Assert.Single(r.Arquivo.Apelidos);
        Assert.Single(r.Arquivo.Nomes);
        Assert.Equal("zz canonica teste", MediaNomeParser.ChaveAgrupamento("zz serie teste"));
        Assert.Equal("ZZ Série Teste Bonita", MediaNomeParser.NomeExibicao("zz canonica teste", "feio"));
        Assert.Equal("diarios de um vampiro", MediaNomeParser.ChaveAgrupamento("DDUV"));   // padrão intacto
    }

    [Fact]
    public void Arquivo_invalido_nao_derruba_e_nao_zera_o_que_ja_valia()
    {
        File.WriteAllText(Arquivo, """{ "apelidos": { "zz serie teste": "zz canonica teste" } }""");
        var servico = Servico();
        servico.Recarregar();

        File.WriteAllText(Arquivo, """{ "apelidos": { "zz serie teste": """);   // editou no meio e salvou quebrado
        var r = servico.Recarregar();

        Assert.Equal(NomesPersonalizadosService.Resultado.Invalido, r.Resultado);
        Assert.Equal("zz canonica teste", MediaNomeParser.ChaveAgrupamento("zz serie teste"));
        Assert.Equal("ataque dos titas", MediaNomeParser.ChaveAgrupamento("Attack on Titan"));
    }

    [Fact]
    public void Arquivo_invalido_na_subida_cai_no_padrao()
    {
        File.WriteAllText(Arquivo, "isto não é json");

        var r = Servico().Recarregar();

        Assert.Equal(NomesPersonalizadosService.Resultado.Invalido, r.Resultado);
        Assert.Equal("ataque dos titas", MediaNomeParser.ChaveAgrupamento("Attack on Titan"));
    }

    [Fact]
    public void Arquivo_removido_volta_ao_padrao()
    {
        File.WriteAllText(Arquivo, """{ "apelidos": { "zz serie teste": "zz canonica teste" } }""");
        var servico = Servico();
        servico.Recarregar();

        File.Delete(Arquivo);
        var r = servico.Recarregar();

        Assert.Equal(NomesPersonalizadosService.Resultado.SemArquivo, r.Resultado);
        Assert.Equal("zz serie teste", MediaNomeParser.ChaveAgrupamento("zz serie teste"));
    }

    [Fact]
    public async Task Trocar_o_arquivo_refaz_a_classificacao_guardada()
    {
        var cache = new ClassificacaoCache();
        var lista = new List<FilmeResponse>
        {
            new(1, "x", null, "Series/ZZ Serie Teste/ZZ.Serie.Teste.S01E01.mkv", false, DateTime.UtcNow, null, null, null, null, null,
                false, false, null, null, null, null, "", "Sem pasta"),
        };
        var antes = cache.Classificar(lista)[0].SerieChave;

        await File.WriteAllTextAsync(Arquivo, """{ "apelidos": { "zz serie teste": "zz canonica teste" } }""");
        Servico().Recarregar();
        var depois = cache.Classificar(lista)[0].SerieChave;

        Assert.NotEqual(antes, depois);
        Assert.Equal("zz canonica teste", depois);
        Assert.Equal(2, cache.Recalculos);
    }
}
