using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

[Collection("NomesGlobais")]
public class SugestoesDeNomesTests
{
    private static List<FilmeResponse> Serie(string nome, string chave, int episodios, int idIni) =>
        Enumerable.Range(1, episodios).Select(e => new FilmeResponse(idIni + e, nome, null, $"{nome}/{e}.mkv", false, DateTime.UtcNow, null, null,
            EhEpisodio: true, Serie: nome, SerieChave: chave, Temporada: 1, Episodio: e)).ToList();

    private static List<SugestaoNome> Analisar(params List<FilmeResponse>[] series) =>
        SugestoesDeNomes.Analisar(series.SelectMany(s => s).ToList());

    [Theory]
    [InlineData("o mentalista", "mentalista", true)]
    [InlineData("hora de aventura", "hora de aventuras", true)]
    [InlineData("rick and morty", "rick and morty", true)]
    [InlineData("serie nova 2", "serie nova 3", false)]
    [InlineData("rick and morty", "hora de aventura", false)]
    [InlineData("lost", "lose", false)]   // curtos demais pra confiar na distância
    public void Parecem_a_mesma_serie(string a, string b, bool esperado) =>
        Assert.Equal(esperado, SugestoesDeNomes.ParecemASerieMesma(a, b));

    [Fact]
    public void Mesma_serie_sugere_apelido_do_menor_pro_maior()
    {
        var s = Assert.Single(Analisar(Serie("O Mentalista", "o mentalista", 100, 0), Serie("Mentalista", "mentalista", 3, 1000)));
        Assert.Equal(("apelidos", "mentalista", "o mentalista"), (s.Secao, s.Chave, s.Valor));
    }

    [Fact]
    public void Nome_todo_em_maiusculas_sugere_caixa_de_titulo()
    {
        var s = Assert.Single(Analisar(Serie("CASA DE PAPEL", "casa de papel", 5, 0)));
        Assert.Equal(("nomes", "casa de papel", "Casa de Papel"), (s.Secao, s.Chave, s.Valor));
    }

    [Fact]
    public void Sigla_curta_e_nome_normal_nao_geram_sugestao() =>
        Assert.Empty(Analisar(Serie("CSI", "csi", 5, 0), Serie("Hora de Aventura", "hora de aventura", 5, 100)));

    [Fact]
    public void Adicionar_preserva_o_resto_do_arquivo_guarda_bak_e_aplica()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var arq = Path.Combine(dir, "nomes.json");
        try
        {
            File.WriteAllText(arq, """{ "ignorar": ["**/Sample/*"], "nomes": { "x y": "X Y" } }""");
            var svc = new NomesPersonalizadosService(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NomesPath"] = arq }).Build(),
                NullLogger<NomesPersonalizadosService>.Instance);

            Assert.Equal(NomesPersonalizadosService.ResultadoAdicao.Aplicado, svc.Adicionar("apelidos", "Mentalista", "o mentalista"));

            var (_, lido) = svc.Recarregar();
            Assert.Equal("o mentalista", lido.Apelidos["mentalista"]);
            Assert.Equal("X Y", lido.Nomes["x y"]);
            Assert.Equal(["**/Sample/*"], lido.Ignorar);
            Assert.True(File.Exists(arq + ".bak"));
            Assert.Equal("o mentalista", MediaNomeParser.ChaveAgrupamento("Mentalista"));
        }
        finally { MediaNomeParser.DefinirNomesPersonalizados(null); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Adicionar_cria_o_arquivo_e_recusa_secao_ou_arquivo_invalido()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var arq = Path.Combine(dir, "nomes.json");
        try
        {
            var svc = new NomesPersonalizadosService(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NomesPath"] = arq }).Build(),
                NullLogger<NomesPersonalizadosService>.Instance);

            Assert.Equal(NomesPersonalizadosService.ResultadoAdicao.SecaoInvalida, svc.Adicionar("ignorar", "a", "b"));
            Assert.Equal(NomesPersonalizadosService.ResultadoAdicao.Aplicado, svc.Adicionar("nomes", "abc def", "Abc Def"));
            Assert.True(File.Exists(arq));

            File.WriteAllText(arq, "{ quebrado");
            Assert.Equal(NomesPersonalizadosService.ResultadoAdicao.ArquivoInvalido, svc.Adicionar("nomes", "x", "y"));
            Assert.Equal("{ quebrado", File.ReadAllText(arq));   // inválido não é tocado
        }
        finally { MediaNomeParser.DefinirNomesPersonalizados(null); Directory.Delete(dir, true); }
    }
}
