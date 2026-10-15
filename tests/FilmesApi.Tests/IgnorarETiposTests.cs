using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary>"ignorar" e "tipos" do nomes.json: corrigir o catálogo sem renomear arquivo (quebra torrent em seed) nem mexer
/// em código. Os dois casos reais: o "Cabo do Medo" que virou série por um S01E10 falso, e a propaganda
/// 1XBET.COM_promo_SHREK_dinheiro_livre.mp4 que apareceu como filme avulso.</summary>
[Collection("NomesGlobais")]
public class IgnorarETiposTests : IDisposable
{
    private readonly string _dir = TestesComuns.CriarPastaTemp();
    public void Dispose()
    {
        MediaNomeParser.DefinirNomesPersonalizados(null);
        Directory.Delete(_dir, recursive: true);
    }

    private static void Aplicar(string json) => MediaNomeParser.DefinirNomesPersonalizados(NomesPersonalizadosService.Ler(json));

    // ─── glob ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("extra/Laranja.Mecanica*/1XBET*", "extra/Laranja.Mecanica.1971.1080p.BluRay.COMANDO.TO/1XBET.COM_promo_SHREK_dinheiro_livre.mp4", true)]
    [InlineData("extra/Laranja.Mecanica*/1XBET*", "extra/Laranja.Mecanica.1971.1080p.BluRay.COMANDO.TO/Laranja.Mecanica.mkv", false)]
    [InlineData("extra/Laranja.Mecanica*/1XBET*", "extra/Outro.Filme/1XBET.mp4", false)]
    [InlineData("**/Sample/*", "Filmes/Matrix/Sample/matrix-sample.mkv", true)]
    [InlineData("**/Sample/*", "Sample/x.mkv", true)]                          // ** vale por zero pastas
    [InlineData("**/Sample/*", "Filmes/Matrix/Sample/dentro/x.mkv", false)]    // * não atravessa pasta
    [InlineData("**/Sample/**", "Filmes/Matrix/Sample/dentro/x.mkv", true)]
    [InlineData("**/sample/*", "Filmes/MATRIX/SAMPLE/x.mkv", true)]            // maiúscula não importa
    [InlineData("Filmes/Alien?.mkv", "Filmes/Alien3.mkv", true)]
    [InlineData("Filmes/Alien?.mkv", "Filmes/Alien.mkv", false)]
    [InlineData("a.b", "aXb", false)]                                          // o ponto é um ponto
    [InlineData("Filmes/Matrix.mkv", "Filmes/Matrix.mkv", true)]               // caminho exato (o que o diagnóstico copia)
    [InlineData("Filmes/Matrix.mkv", "Filmes/Matrix.mkv.part", false)]
    public void Glob(string padrao, string caminho, bool casa) =>
        Assert.Equal(casa, GlobCaminho.ParaRegex(padrao).IsMatch(caminho));

    [Fact]
    public void Parenteses_e_colchetes_no_nome_sao_literais()
    {
        const string caminho = "Series/Show [1080p] (2020)/ep.mkv";
        Assert.Matches(GlobCaminho.ParaRegex(caminho), caminho);
    }

    // ─── formato do arquivo ──────────────────────────────────────────────────────────

    [Fact]
    public void Le_ignorar_e_tipos()
    {
        var arq = NomesPersonalizadosService.Ler("""{ "ignorar": ["a/*", "**/Sample/*"], "tipos": { "Cabo do Medo": "filme" } }""");
        Assert.Equal(["a/*", "**/Sample/*"], arq.Ignorar);
        Assert.Equal("filme", arq.Tipos["Cabo do Medo"]);
    }

    [Theory]
    [InlineData("""{ "ignorar": "texto" }""")]
    [InlineData("""{ "ignorar": [1] }""")]
    [InlineData("""{ "tipos": { "x": "serie" } }""")]      // só "filme" é aceito: erro de digitação não passa em silêncio
    [InlineData("""{ "tipos": { "x": 3 } }""")]
    public void Ignorar_ou_tipos_fora_do_formato_lanca(string json) =>
        Assert.ThrowsAny<Exception>(() => NomesPersonalizadosService.Ler(json));

    // ─── "tipos": filme forçado ──────────────────────────────────────────────────────

    private const string CaboDoMedo = "Series/Cabo do Medo/Cabo.do.Medo.1991.S01E10.1080p.BluRay.x264.mkv";

    [Fact]
    public void Cabo_do_Medo_sem_tipos_vira_serie_com_um_episodio()
    {
        var c = MediaNomeParser.Classificar(CaboDoMedo, "Cabo do Medo");
        Assert.True(c.EhEpisodio);
    }

    [Fact]
    public void Cabo_do_Medo_com_tipos_vira_filme_avulso()
    {
        Aplicar("""{ "tipos": { "cabo do medo": "filme" } }""");

        var c = MediaNomeParser.Classificar(CaboDoMedo, "Cabo do Medo");

        Assert.False(c.EhEpisodio);
        Assert.Null(c.Serie);
        Assert.Null(c.Temporada);
        Assert.Equal("Cabo do Medo", c.Rotulo);
    }

    [Theory]
    [InlineData("cabo do medo")]
    [InlineData("  CABO  do  MEDO ")]     // maiúscula e espaço extra não importam
    [InlineData("Cabo do Médo")]          // nem acento
    public void Tipos_aceita_acento_maiuscula_e_espaco_na_chave(string chave)
    {
        Aplicar("{ \"tipos\": { \"" + chave + "\": \"filme\" } }");
        Assert.False(MediaNomeParser.Classificar(CaboDoMedo, "x").EhEpisodio);
    }

    [Fact]
    public void Tipos_de_outra_serie_nao_mexe_nessa()
    {
        Aplicar("""{ "tipos": { "cabo do medo 2": "filme" } }""");
        Assert.True(MediaNomeParser.Classificar(CaboDoMedo, "x").EhEpisodio);
    }

    [Fact]
    public void Tipos_nao_atrapalha_as_outras_series()
    {
        Aplicar("""{ "tipos": { "cabo do medo": "filme" } }""");
        Assert.True(MediaNomeParser.Classificar("Series/Breaking Bad/Breaking.Bad.S01E01.720p.mkv", "x").EhEpisodio);
    }

    [Fact]
    public void Tipos_muda_a_classificacao_guardada()
    {
        var cache = new ClassificacaoCache();
        var lista = new List<FilmeResponse>
        {
            new(1, "Cabo do Medo", null, CaboDoMedo, false, DateTime.UtcNow, null, null, null, null, null,
                false, false, null, null, null, null, "", "Sem pasta"),
        };
        Assert.True(cache.Classificar(lista)[0].EhEpisodio);

        Aplicar("""{ "tipos": { "cabo do medo": "filme" } }""");

        Assert.False(cache.Classificar(lista)[0].EhEpisodio);
    }

    // ─── "ignorar" ───────────────────────────────────────────────────────────────────

    private const string PropagandaNova = "extra/Laranja.Mecanica.1971.1080p.BluRay.COMANDO.TO/1XBET.COM_promo_SHREK_dinheiro_livre.mp4";
    private const string PropagandaTorrent = "Contos do Loop/1XBET.COM_promo_SHREK_dinheiro_livre.mp4";
    private const string Filme = "extra/Laranja.Mecanica.1971.1080p.BluRay.COMANDO.TO/Laranja.Mecanica.1971.mkv";

    [Fact]
    public void Ignorado_responde_pelo_caminho_relativo()
    {
        Aplicar("""{ "ignorar": ["extra/Laranja.Mecanica*/1XBET*", "Contos do Loop/1XBET*"] }""");

        Assert.True(MediaNomeParser.Ignorado(PropagandaNova));
        Assert.True(MediaNomeParser.Ignorado(PropagandaTorrent));
        Assert.False(MediaNomeParser.Ignorado(Filme));
        Assert.False(MediaNomeParser.Ignorado(null));
    }

    [Fact]
    public void Sem_arquivo_nada_e_ignorado() =>
        Assert.False(MediaNomeParser.Ignorado(PropagandaNova));

    [Fact]
    public async Task Scan_nao_importa_o_ignorado_e_o_arquivo_continua_no_disco()
    {
        using var f = new ScanFixture();
        f.Arquivo(PropagandaNova);
        f.Arquivo(PropagandaTorrent);
        f.Arquivo(Filme);
        Aplicar("""{ "ignorar": ["extra/Laranja.Mecanica*/1XBET*", "Contos do Loop/1XBET*"] }""");

        var r = await f.ScanAsync();

        Assert.Equal(1, r.Importados);
        Assert.Equal([Filme], await f.CaminhosNoBancoAsync());
        Assert.True(File.Exists(Path.Combine(f.Media, PropagandaNova)));     // não apagou nada do disco
        Assert.True(File.Exists(Path.Combine(f.Media, PropagandaTorrent)));  // o torrent em seed segue intacto
    }

    [Fact]
    public async Task Ignorar_depois_de_importado_some_da_lista_e_do_diagnostico()
    {
        using var f = new ScanFixture();
        f.Arquivo(PropagandaNova, new string('x', 1000));   // pequeno, nome de uploader: suspeito de propaganda
        f.Arquivo(Filme);
        await f.ScanAsync();

        var antes = await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().DiagnosticoAsync());
        Assert.Contains(antes.SuspeitosDePropaganda, s => s.Arquivo.Caminho == PropagandaNova);

        Aplicar("""{ "ignorar": ["extra/Laranja.Mecanica*/1XBET*"] }""");
        var tela = await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterTelaAsync("all", "all", null));
        var depois = await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().DiagnosticoAsync());

        Assert.DoesNotContain(depois.SuspeitosDePropaganda, s => s.Arquivo.Caminho == PropagandaNova);
        Assert.Equal(1, depois.TotalArquivos);
        Assert.DoesNotContain(TodosOsCaminhos(tela), c => c == PropagandaNova);
        Assert.Contains(TodosOsCaminhos(tela), c => c == Filme);
    }

    [Fact]
    public async Task Diagnostico_traz_a_entrada_pronta_pra_colar_no_ignorar()
    {
        using var f = new ScanFixture();
        f.Arquivo(PropagandaTorrent, new string('x', 1000));
        await f.ScanAsync();

        var d = await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().DiagnosticoAsync());

        var suspeito = Assert.Single(d.SuspeitosDePropaganda);
        Assert.Equal("\"Contos do Loop/1XBET.COM_promo_SHREK_dinheiro_livre.mp4\"", suspeito.EntradaIgnorar);
        // e essa entrada, colada no ignorar, de fato esconde o arquivo
        Aplicar("{ \"ignorar\": [" + suspeito.EntradaIgnorar + "] }");
        Assert.True(MediaNomeParser.Ignorado(PropagandaTorrent));
    }

    private static IEnumerable<string?> TodosOsCaminhos(TelaCatalogoResponse t) =>
        t.FilmesSoltos.Concat(t.PastasFilme.SelectMany(p => p.Itens)).Concat(t.Series.SelectMany(s => s.Episodios)).Select(f => f.ArquivoPath);
}
