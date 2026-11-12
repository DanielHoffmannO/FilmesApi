using System.Text.Json;
using FilmesApi.Controllers;
using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>"Dublado" (áudio em português que não é IA): a dica pelo nome do arquivo, a leitura das faixas do ffprobe, a precedência entre as duas,
/// o filtro <c>dublagem=</c> do catálogo, a palavra "dublado" na busca, a fileira <c>/api/catalogo/dublados</c> e a coluna nova de
/// <c>AnalisesDeDublagem</c>. A dublagem por IA em si é testada em <c>DubladoIaTests</c>.</summary>
public class DubladoPtTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    // ─── dica pelo nome ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("O.Mentalista.S05E17.720p.WEB-DL.x264.Dublado-WWW.BLUDV.COM.mkv", true)]
    [InlineData("Filme (2020) BluRay 1080p DUAL 5.1", true)]
    [InlineData("Pelicula.PT-BR.mkv", true)]
    [InlineData("Titanic 1997 Legendado", false)]
    [InlineData("Dualidade (2019)", false)]
    [InlineData("Filme.2019.1080p.Dual.Audio.mkv", true)]
    [InlineData("Filme.2019.1080p.Dual-Áudio.mkv", true)]
    [InlineData("Filme.2019.DualAudio.mkv", true)]
    [InlineData("Series/Seriado Dublado/S01E01.mkv", true)]            // a pasta também conta
    [InlineData("Filme.Dublada.2019.mkv", true)]
    [InlineData("Filme.Dublagem.Classica.mkv", true)]
    [InlineData("Filme [Dubl] 2019.mkv", true)]
    [InlineData("Filme Nacional 2019.mkv", true)]
    [InlineData("Filme.Ptbr.2019.mkv", true)]
    [InlineData("Filme Em Português.mkv", true)]                       // acento não importa
    [InlineData("FILME.DUBLADO.mkv", true)]                            // caixa não importa
    [InlineData("Dublin Murders S01E01.mkv", false)]                   // "dubl" só como palavra inteira
    [InlineData("Filme Internacional 2019.mkv", false)]
    [InlineData("Dualismo.2019.mkv", false)]
    [InlineData("Filme.2019.Legendado.PT-BR.mkv", false)]              // o PT-BR é da legenda
    [InlineData("Filme.2019.Legendas.em.Portugues.mkv", false)]
    [InlineData("Filme.2019.Subs.PTBR.mkv", false)]
    [InlineData("Filme.2019.Dublado.Legendado.PT-BR.mkv", true)]       // dublado E com legenda: vale o dublado
    [InlineData("Filme.2019.1080p.BluRay.x264.mkv", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Dica_no_nome_acha_dublado_dual_nacional_e_ptbr_sem_casar_legendado_nem_palavra_parecida(string? caminho, bool esperado) =>
        Assert.Equal(esperado, DublagemEmPortugues.DicaNoNome(caminho));

    // ─── leitura das faixas ──────────────────────────────────────────────────────────

    private static FaixaAudio Faixa(string? idioma, string? titulo = null) => new(1, "aac", idioma, false, 2, titulo);

    [Theory]
    [InlineData("por", AudioPortugues.Sim)]
    [InlineData("pt", AudioPortugues.Sim)]
    [InlineData("pob", AudioPortugues.Sim)]
    [InlineData("POR", AudioPortugues.Sim)]
    [InlineData("pt-BR", AudioPortugues.Sim)]
    [InlineData("eng", AudioPortugues.Nao)]
    [InlineData("jpn", AudioPortugues.Nao)]
    [InlineData(null, AudioPortugues.Indefinido)]
    [InlineData("", AudioPortugues.Indefinido)]
    [InlineData("und", AudioPortugues.Indefinido)]
    public void Faixa_unica_em_portugues_e_sim_sem_idioma_e_indefinida_e_outro_idioma_e_nao(string? idioma, AudioPortugues esperado) =>
        Assert.Equal(esperado, DublagemEmPortugues.ClassificarAudios([Faixa(idioma)]));

    [Fact]
    public void A_faixa_de_ia_nao_conta_como_dublagem_de_verdade()
    {
        Assert.Equal(AudioPortugues.Nao, DublagemEmPortugues.ClassificarAudios([Faixa("jpn"), Faixa("por", "Português (IA)")]));
        Assert.Equal(AudioPortugues.Sim, DublagemEmPortugues.ClassificarAudios([Faixa("jpn"), Faixa("por", "Português (IA)"), Faixa("por", "Português 5.1")]));
        Assert.Equal(AudioPortugues.Nao, DublagemEmPortugues.ClassificarAudios([Faixa("por", "Dublagem IA)")]));
    }

    [Fact]
    public void Faixa_sem_idioma_nunca_vira_portugues_so_deixa_o_probe_indefinido()
    {
        Assert.Equal(AudioPortugues.Indefinido, DublagemEmPortugues.ClassificarAudios([Faixa("eng"), Faixa(null)]));
        Assert.Equal(AudioPortugues.Sim, DublagemEmPortugues.ClassificarAudios([Faixa(null), Faixa("por")]));   // uma marcada como português basta
        Assert.Equal(AudioPortugues.Nao, DublagemEmPortugues.ClassificarAudios([]));                            // sem áudio nenhum
        Assert.Equal(AudioPortugues.Nao, DublagemEmPortugues.ClassificarAudios([Faixa("eng"), Faixa("jpn")]));
    }

    // ─── precedência: probe manda sobre o nome ───────────────────────────────────────

    [Theory]
    [InlineData(null, "Filme.Dublado.mkv", true)]                       // ainda não analisado: a dica
    [InlineData(null, "Filme.mkv", false)]
    [InlineData(AudioPortugues.Sim, "Filme.mkv", true)]                 // probe acha português mesmo sem pista no nome
    [InlineData(AudioPortugues.Nao, "Filme.Dublado.mkv", false)]        // probe diz que não tem: ganha da dica do nome
    [InlineData(AudioPortugues.Nao, "Filme.DUAL.mkv", false)]
    [InlineData(AudioPortugues.Indefinido, "Filme.DUAL.mkv", true)]     // faixas sem idioma: só a dica vale
    [InlineData(AudioPortugues.Indefinido, "Filme.mkv", false)]
    public void Probe_manda_sobre_a_dica_do_nome_exceto_quando_nao_diz_nada(AudioPortugues? probe, string caminho, bool esperado) =>
        Assert.Equal(esperado, DublagemEmPortugues.Resolver(probe, caminho));

    // ─── filtro dublagem= e palavra na busca ─────────────────────────────────────────

    private static readonly DateTime Agora = new(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);

    private static FilmeResponse Filme(int id, string titulo, bool dub = false, bool ia = false, bool assistido = false, double diasAtras = 1, bool extra = false) =>
        new(id, titulo, null, null, assistido, Agora.AddDays(-diasAtras), null, null, EhExtra: extra, DubladoIa: ia, Dublado: dub);

    private static FilmeResponse Ep(int id, string serie, int temp, int ep, bool dub = true, bool ia = false, bool assistido = false, double diasAtras = 1, string? titulo = null) =>
        new(id, $"{serie} S{temp:00}E{ep:00}", null, null, assistido, Agora.AddDays(-diasAtras), null, null,
            EhEpisodio: true, Serie: serie, SerieChave: serie.ToLowerInvariant(), Temporada: temp, Episodio: ep,
            Rotulo: $"S{temp:00}E{ep:00}", TituloEpisodio: titulo, DubladoIa: ia, Dublado: dub);

    private static List<FilmeResponse> Catalogo() =>
    [
        Filme(1, "Interestelar", dub: true),
        Filme(2, "Matrix"),
        Filme(3, "Akira", ia: true),
        Filme(4, "Parasita", dub: true, ia: true),
        Ep(10, "Mentalista", 1, 1), Ep(11, "Mentalista", 1, 2), Ep(12, "Mentalista", 1, 3, dub: false),
        Ep(20, "Naruto", 1, 1, dub: false, ia: true),
    ];

    private static List<int> Ids(TelaCatalogoResponse t) =>
        [.. t.FilmesSoltos.Select(f => f.Id), .. t.PastasFilme.SelectMany(p => p.Itens).Select(f => f.Id), .. t.Series.SelectMany(s => s.Episodios).Select(f => f.Id)];

    private static TelaCatalogoResponse Tela(string tipo = "all", string visto = "all", string? busca = null, string? dublagem = null) =>
        CatalogoAgrupador.Montar(Catalogo(), [], tipo, visto, busca, dublagem);

    [Fact]
    public void Sem_o_parametro_o_catalogo_vem_inteiro_como_antes() =>
        Assert.Equal([1, 2, 3, 4, 10, 11, 12, 20], Ids(Tela()).Order());

    [Theory]
    [InlineData("qualquer", new[] { 1, 3, 4, 10, 11, 20 })]
    [InlineData("QUALQUER", new[] { 1, 3, 4, 10, 11, 20 })]
    [InlineData("dublado", new[] { 1, 4, 10, 11 })]
    [InlineData("ia", new[] { 3, 4, 20 })]
    [InlineData("", new[] { 1, 2, 3, 4, 10, 11, 12, 20 })]            // vazio/desconhecido = sem filtro, como parâmetro ausente
    [InlineData("xyz", new[] { 1, 2, 3, 4, 10, 11, 12, 20 })]
    public void Parametro_dublagem_filtra_por_dublado_ia_ou_qualquer(string dublagem, int[] esperados) =>
        Assert.Equal(esperados, Ids(Tela(dublagem: dublagem)).Order());

    [Fact]
    public void Dublagem_combina_com_tipo_visto_e_busca()
    {
        Assert.Equal([1, 3, 4], Ids(Tela(tipo: "filme", dublagem: "qualquer")).Order());
        Assert.Equal([10, 11, 20], Ids(Tela(tipo: "serie", dublagem: "qualquer")).Order());
        Assert.Equal([1], Ids(Tela(busca: "intere", dublagem: "qualquer")));
        Assert.Empty(Ids(Tela(busca: "matrix", dublagem: "qualquer")));   // Matrix não é dublado
        Assert.Equal([2], Ids(Tela(busca: "matrix", dublagem: "xyz")));   // filtro desconhecido: só a busca, como sempre

        var vistos = Catalogo().Select(f => f.Id == 1 ? f with { Assistido = true } : f).ToList();
        Assert.Equal([3, 4, 10, 11, 20], Ids(CatalogoAgrupador.Montar(vistos, [], "all", "nao-assistido", null, "qualquer")).Order());
        Assert.Equal([1], Ids(CatalogoAgrupador.Montar(vistos, [], "all", "assistido", null, "qualquer")));
    }

    [Fact]
    public void Serie_so_traz_os_episodios_que_passam_no_filtro()
    {
        var serie = Assert.Single(Tela(dublagem: "dublado").Series);
        Assert.Equal([10, 11], serie.Episodios.Select(e => e.Id));
    }

    [Fact]
    public void Continuar_assistindo_tambem_respeita_o_filtro()
    {
        var candidatos = new List<FilmeResponse> { Filme(1, "Interestelar", dub: true), Filme(2, "Matrix") };
        var tela = CatalogoAgrupador.Montar(Catalogo(), candidatos, "all", "all", null, "qualquer");
        Assert.Equal([1], tela.ContinuarAssistindo.Select(f => f.Id));
    }

    [Theory]
    [InlineData("dublado")]
    [InlineData("dublados")]
    [InlineData("Dublados")]
    [InlineData("  DUBLADO ")]
    [InlineData("dublada")]
    public void Digitar_dublado_na_busca_liga_o_mesmo_filtro(string busca)
    {
        Assert.Equal([1, 3, 4, 10, 11, 20], Ids(Tela(busca: busca)).Order());
        Assert.False(Tela(busca: busca).CatalogoVazio);
    }

    [Fact]
    public void Palavra_dublado_mais_titulo_filtra_e_procura_o_titulo()
    {
        Assert.Equal([10, 11], Ids(Tela(busca: "dublados mentalista")).Order());
        Assert.Equal([10, 11], Ids(Tela(busca: "Mentalista dublado")).Order());
        Assert.Empty(Ids(Tela(busca: "dublado matrix")));
    }

    [Fact]
    public void Busca_por_titulo_normal_continua_igual()
    {
        Assert.Equal([2], Ids(Tela(busca: "matrix")));
        Assert.Equal([1], Ids(Tela(busca: "INTERESTELAR")));
        Assert.Equal([10, 11, 12], Ids(Tela(busca: "mentalista")).Order());
        Assert.Equal([1, 2, 3, 4, 10, 11, 12, 20], Ids(Tela(busca: "   ")).Order());
        Assert.Empty(Ids(Tela(busca: "dublad")));   // meia palavra é texto, não o filtro
    }

    [Fact]
    public void Parametro_explicito_vence_a_palavra_da_busca()
    {
        Assert.Equal([3, 4, 20], Ids(Tela(busca: "dublado", dublagem: "ia")).Order());
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("matrix", "matrix", null)]
    [InlineData("dublado", null, "qualquer")]
    [InlineData("Dublados", null, "qualquer")]
    [InlineData("dublado matrix", "matrix", "qualquer")]
    [InlineData("O Dublado", "o", "qualquer")]
    [InlineData("dublagem", "dublagem", null)]
    public void ExtrairDaBusca_separa_a_palavra_de_idioma_do_texto(string? busca, string? texto, string? dublagem)
    {
        var (t, d) = DublagemEmPortugues.ExtrairDaBusca(busca);
        Assert.Equal(texto, t);
        Assert.Equal(dublagem, d);
    }

    // ─── DTO ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filme_response_traz_dublado_com_default_falso_e_sai_em_camelCase()
    {
        var filme = new FilmeResponse(1, "X", null, "a.mkv", false, DateTime.UtcNow, null, null);
        Assert.False(filme.Dublado);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Contains("\"dublado\":false", JsonSerializer.Serialize(filme, web));
        Assert.Contains("\"dublado\":true", JsonSerializer.Serialize(filme with { Dublado = true }, web));
    }

    // ─── a fileira: um cartão por série ──────────────────────────────────────────────

    [Fact]
    public void Nada_dublado_nao_tem_fileira() =>
        Assert.Empty(Dublados.Montar([Filme(1, "Matrix"), Ep(2, "Série", 1, 1, dub: false)]));

    [Fact]
    public void Catalogo_vazio_e_limite_zero_nao_tem_fileira()
    {
        Assert.Empty(Dublados.Montar([]));
        Assert.Empty(Dublados.Montar([Filme(1, "X", dub: true)], 0));
    }

    [Fact]
    public void Filme_solto_dublado_vira_cartao_simples_com_o_titulo()
    {
        var n = Assert.Single(Dublados.Montar([Filme(1, "Interestelar", dub: true), Filme(2, "Matrix")]));
        Assert.Equal((1, 1, "Interestelar"), (n.Filme.Id, n.Novos, n.Rotulo));
    }

    [Fact]
    public void Episodios_dublados_da_serie_colapsam_apontando_pro_proximo_nao_visto()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(1, "Mentalista", 1, 1, assistido: true), Ep(2, "Mentalista", 1, 2), Ep(3, "Mentalista", 1, 3), Ep(4, "Mentalista", 2, 1),
            Ep(5, "Mentalista", 1, 4, dub: false),   // este não é dublado: não conta nem é alvo
        };

        var n = Assert.Single(Dublados.Montar(todos));

        Assert.Equal(2, n.Filme.Id);                          // o primeiro dublado ainda não visto, na ordem temporada/episódio
        Assert.Equal(3, n.Novos);                             // por ver: 1x02, 1x03, 2x01
        Assert.Equal("Mentalista · 4 episódios dublados", n.Rotulo);   // dublados no total
    }

    [Fact]
    public void Episodio_dublado_unico_mostra_o_codigo_e_o_titulo()
    {
        Assert.Equal("Série · S02E05", Assert.Single(Dublados.Montar([Ep(1, "Série", 2, 5)])).Rotulo);
        Assert.Equal("Série · S02E05 · O Apagador", Assert.Single(Dublados.Montar([Ep(1, "Série", 2, 5, titulo: "O Apagador")])).Rotulo);
    }

    [Fact]
    public void Serie_toda_vista_aponta_pro_primeiro_dublado_com_novos_zero_e_vai_pro_fim()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(1, "Vista", 1, 1, assistido: true, diasAtras: 1), Ep(2, "Vista", 1, 2, assistido: true, diasAtras: 1),   // mais recente, mas já vista
            Filme(3, "Antigo por ver", dub: true, diasAtras: 100),
        };

        var itens = Dublados.Montar(todos);

        Assert.Equal([3, 1], itens.Select(n => n.Filme.Id));
        Assert.Equal(0, itens[1].Novos);
        Assert.Equal("Vista · 2 episódios dublados", itens[1].Rotulo);
    }

    [Fact]
    public void Ordem_por_ver_primeiro_e_depois_os_mais_recentes()
    {
        var todos = new List<FilmeResponse>
        {
            Filme(1, "Velho", dub: true, diasAtras: 30),
            Filme(2, "Novo", dub: true, diasAtras: 1),
            Filme(3, "Visto novíssimo", dub: true, assistido: true, diasAtras: 0.5),
            Ep(10, "Serie", 1, 1, diasAtras: 5), Ep(11, "Serie", 1, 2, diasAtras: 2),   // cartão da série vale a data do episódio mais recente: 2 dias
        };

        Assert.Equal([2, 10, 1, 3], Dublados.Montar(todos).Select(n => n.Filme.Id));
    }

    [Fact]
    public void Ia_conta_extra_e_ignorado_e_o_limite_corta_depois_de_colapsar()
    {
        var todos = new List<FilmeResponse>
        {
            Filme(1, "Akira", ia: true, diasAtras: 0.5), Filme(2, "Trailer", dub: true, extra: true), Filme(3, "Matrix"),
            Ep(10, "S", 1, 1), Ep(11, "S", 1, 2), Ep(12, "S", 1, 3), Filme(4, "Outro", dub: true, diasAtras: 9),
        };

        var tudo = Dublados.Montar(todos);
        Assert.Equal([1, 10, 4], tudo.Select(n => n.Filme.Id));   // 3 cartões: a série tem 3 episódios mas é 1 cartão; o trailer e o Matrix ficam de fora
        Assert.Equal(["Akira", "S · 3 episódios dublados", "Outro"], tudo.Select(n => n.Rotulo));
        Assert.Equal([1, 10], Dublados.Montar(todos, 2).Select(n => n.Filme.Id));
    }

    [Fact]
    public void Itens_saem_no_mesmo_formato_da_fileira_de_novidades_em_json()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(Dublados.Montar([Filme(1, "Interestelar", dub: true)]), web);
        Assert.Contains("\"filme\":{", json);
        Assert.Contains("\"novos\":1", json);
        Assert.Contains("\"rotulo\":\"Interestelar\"", json);
        Assert.Contains("\"dublado\":true", json);
    }

    // ─── ponta a ponta: probe de mentira, banco de verdade ───────────────────────────

    private const string Video = """{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720}""";
    private const string AudioEng = """{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"disposition":{"default":1},"tags":{"language":"eng"}}""";
    private const string AudioPor = """{"index":2,"codec_type":"audio","codec_name":"ac3","channels":2,"disposition":{"default":0},"tags":{"language":"por"}}""";
    private const string AudioPorIa = """{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"disposition":{"default":0},"tags":{"language":"por","title":"Português (IA)"}}""";
    private const string AudioSemIdioma = """{"index":1,"codec_type":"audio","codec_name":"ac3","channels":6,"disposition":{"default":1}}""";

    private static string Json(params string[] streams) => "{\"streams\":[" + Video + "," + string.Join(",", streams) + "],\"format\":{\"duration\":\"1400.0\"}}";

    /// <summary>ffprobe falso, pelo CONTEÚDO do arquivo: "TEMPT" = inglês + português; "TEMIA" = inglês + português por IA; "SEMIDIOMA" = uma faixa
    /// sem idioma; qualquer outro = só inglês. Cada chamada fica anotada em <c>ffprobe-pt.sh.chamadas</c>.</summary>
    private string FfprobePt() => TestesComuns.ScriptFalso(_bin, "ffprobe-pt.sh", $$"""
        for a; do arq="$a"; done
        echo "$arq" >> "$0.chamadas"
        if grep -q TEMPT "$arq"; then echo '{{Json(AudioEng, AudioPor)}}'
        elif grep -q TEMIA "$arq"; then echo '{{Json(AudioEng, AudioPorIa)}}'
        elif grep -q SEMIDIOMA "$arq"; then echo '{{Json(AudioSemIdioma)}}'
        else echo '{{Json(AudioEng)}}'
        fi
        """);

    private int Chamadas() => File.Exists(Path.Combine(_bin, "ffprobe-pt.sh.chamadas")) ? File.ReadAllLines(Path.Combine(_bin, "ffprobe-pt.sh.chamadas")).Length : 0;

    private async Task<ScanFixture> Cenario(params (string Arquivo, string Conteudo)[] arquivos)
    {
        var f = new ScanFixture(ffprobe: FfprobePt());
        _fixtures.Add(f);
        foreach (var (arquivo, conteudo) in arquivos) f.Arquivo(arquivo, conteudo);
        await f.ScanAsync();
        return f;
    }

    private static DubladoIaService Servico(ScanFixture f, Dictionary<string, string?>? extra = null) =>
        new(f.Escopos, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["MediaPath"] = f.Media }.Concat(extra ?? []).ToDictionary(kv => kv.Key, kv => kv.Value)).Build(),
            NullLogger<DubladoIaService>.Instance);

    private static async Task<Dictionary<string, FilmeResponse>> PorCaminho(ScanFixture f) =>
        (await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ListarAsync())).ToDictionary(x => x.ArquivoPath!);

    private static Task<List<AnaliseDeDublagem>> Linhas(ScanFixture f) =>
        f.NoEscopoAsync(sp => sp.GetRequiredService<AppDbContext>().AnalisesDeDublagem.AsNoTracking().OrderBy(a => a.FilmeId).ToListAsync());

    [Fact]
    public async Task Antes_da_analise_vale_a_dica_do_nome_e_depois_o_probe_manda_sem_ffprobe_na_listagem()
    {
        var f = await Cenario(
            ("Filmes/Filme.Dublado.2020.mkv", "so ingles"),        // nome diz dublado, o arquivo não tem português
            ("Filmes/Filme.Qualquer.2021.mkv", "TEMPT"),           // nome sem pista, o arquivo tem português
            ("Filmes/Filme.Dual.2022.mkv", "SEMIDIOMA"),           // nome diz dual, faixa sem idioma: só a dica vale
            ("Filmes/Filme.SemDica.2023.mkv", "SEMIDIOMA"),        // nada diz nada
            ("Anime/Ep.mkv", "TEMIA"));                            // só a IA em português: Dublado é falso, DubladoIa é verdadeiro

        for (var i = 0; i < 3; i++)
        {
            var antes = await PorCaminho(f);
            Assert.True(antes["Filmes/Filme.Dublado.2020.mkv"].Dublado);
            Assert.False(antes["Filmes/Filme.Qualquer.2021.mkv"].Dublado);
            Assert.True(antes["Filmes/Filme.Dual.2022.mkv"].Dublado);
            Assert.All(antes.Values, x => Assert.False(x.DubladoIa));
        }
        Assert.Equal(0, Chamadas());   // listar nunca roda ffprobe

        Assert.Equal(5, await Servico(f).ExecutarUmaVezAsync());

        for (var i = 0; i < 3; i++)
        {
            var depois = await PorCaminho(f);
            Assert.False(depois["Filmes/Filme.Dublado.2020.mkv"].Dublado);    // o probe ganhou da dica
            Assert.True(depois["Filmes/Filme.Qualquer.2021.mkv"].Dublado);
            Assert.True(depois["Filmes/Filme.Dual.2022.mkv"].Dublado);        // indefinido: a dica segue valendo
            Assert.False(depois["Filmes/Filme.SemDica.2023.mkv"].Dublado);
            Assert.False(depois["Anime/Ep.mkv"].Dublado);
            Assert.True(depois["Anime/Ep.mkv"].DubladoIa);
        }
        Assert.Equal(5, Chamadas());   // um ffprobe por arquivo, só na análise

        var linhas = (await Linhas(f)).ToDictionary(a => a.FilmeId);
        async Task<AudioPortugues?> Guardado(string caminho) => linhas[await f.IdDeAsync(caminho)].AudioPtNaoIa;
        Assert.Equal(AudioPortugues.Nao, await Guardado("Filmes/Filme.Dublado.2020.mkv"));
        Assert.Equal(AudioPortugues.Sim, await Guardado("Filmes/Filme.Qualquer.2021.mkv"));
        Assert.Equal(AudioPortugues.Indefinido, await Guardado("Filmes/Filme.Dual.2022.mkv"));
        Assert.Equal(AudioPortugues.Indefinido, await Guardado("Filmes/Filme.SemDica.2023.mkv"));
        Assert.Equal(AudioPortugues.Nao, await Guardado("Anime/Ep.mkv"));   // a faixa em português dele é a de IA
    }

    [Fact]
    public async Task Linha_antiga_sem_a_coluna_volta_a_fila_depois_dos_novos_e_mantem_o_dubladoIa_enquanto_isso()
    {
        var f = await Cenario(("Anime/Ep.mkv", "TEMIA"), ("Filmes/Velho.mkv", "TEMPT"));
        var servico = Servico(f);
        Assert.Equal(2, await servico.ExecutarUmaVezAsync());
        Assert.Equal(0, await servico.ExecutarUmaVezAsync());   // em dia

        // simula o banco de antes da coluna: tudo NULL
        await f.NoEscopoAsync(async sp => await sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("""UPDATE "AnalisesDeDublagem" SET "AudioPtNaoIa" = NULL;"""));
        Assert.All(await Linhas(f), a => Assert.Null(a.AudioPtNaoIa));
        Assert.True((await PorCaminho(f))["Anime/Ep.mkv"].DubladoIa);        // o flag de IA continua valendo
        Assert.False((await PorCaminho(f))["Filmes/Velho.mkv"].Dublado);    // e sem probe nem dica, não é dublado ainda

        // chega um arquivo novo: com lote de 1, ele passa na frente das linhas antigas
        f.Arquivo("Filmes/Novo.mkv", "TEMPT");
        await f.ScanAsync();
        var lote1 = Servico(f, new() { ["DubladoIaLoteMax"] = "1" });
        Assert.Equal(1, await lote1.ExecutarUmaVezAsync());
        Assert.Equal(2, lote1.Pendentes);
        var depois1 = (await Linhas(f)).ToDictionary(a => a.FilmeId);
        var idNovo = await f.IdDeAsync("Filmes/Novo.mkv");
        Assert.Equal(AudioPortugues.Sim, depois1[idNovo].AudioPtNaoIa);
        Assert.Equal(2, depois1.Values.Count(a => a.AudioPtNaoIa is null));

        // as duas antigas são refeitas nas rodadas seguintes e depois fica em dia
        Assert.Equal(2, await Servico(f).ExecutarUmaVezAsync());
        Assert.All(await Linhas(f), a => Assert.NotNull(a.AudioPtNaoIa));
        Assert.True((await PorCaminho(f))["Filmes/Velho.mkv"].Dublado);
        Assert.Equal(0, await Servico(f).ExecutarUmaVezAsync());
    }

    [Fact]
    public async Task Arquivo_trocado_e_reanalisado_tambem_pro_portugues()
    {
        var f = await Cenario(("Filmes/Filme.mkv", "so ingles"));
        var servico = Servico(f);
        Assert.Equal(1, await servico.ExecutarUmaVezAsync());
        Assert.False((await PorCaminho(f))["Filmes/Filme.mkv"].Dublado);
        Assert.Equal(0, await servico.ExecutarUmaVezAsync());

        f.Arquivo("Filmes/Filme.mkv", "TEMPT numa versão nova, com a faixa dublada e bem maior");
        Assert.Equal(1, await servico.ExecutarUmaVezAsync());
        Assert.True((await PorCaminho(f))["Filmes/Filme.mkv"].Dublado);
        Assert.Equal(AudioPortugues.Sim, Assert.Single(await Linhas(f)).AudioPtNaoIa);
    }

    [Fact]
    public async Task Tela_e_fileira_ponta_a_ponta_com_o_banco()
    {
        var f = await Cenario(
            ("Interestelar.2014.Dublado.mkv", "so ingles"),    // dica do nome (probe ainda não rodou)
            ("Matrix.1999.mkv", "TEMPT"),
            ("Alien.1979.mkv", "so ingles"),
            ("Series/Serie.S01E01.mkv", "so ingles"), ("Series/Serie.S01E02.mkv", "so ingles"));
        await Servico(f).ExecutarUmaVezAsync();   // agora o probe manda: Interestelar deixa de ser dublado, Matrix passa a ser

        var (tela, fileira) = await f.NoEscopoAsync(async sp =>
        {
            var servico = sp.GetRequiredService<FilmeService>();
            var t = await servico.ObterTelaAsync("all", "all", null, "qualquer");
            var ok = Assert.IsType<OkObjectResult>(await new DubladosController(servico).ObterDublados(24));
            return (t, Assert.IsType<List<ItemNovidade>>(ok.Value));
        });

        Assert.Equal(["Matrix.1999.mkv"], tela.FilmesSoltos.Select(x => x.ArquivoPath));
        Assert.Empty(tela.PastasFilme);
        Assert.Empty(tela.Series);
        var n = Assert.Single(fileira);
        Assert.Equal(("Matrix.1999.mkv", 1), (n.Filme.ArquivoPath, n.Novos));

        // sem o parâmetro, a tela é a de sempre
        var todos = await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterTelaAsync("all", "all", null));
        Assert.Equal(3, todos.FilmesSoltos.Count);
        Assert.Single(todos.Series);
    }

    [Fact]
    public async Task Endpoint_dublados_respeita_o_limite_e_o_teto()
    {
        var f = await Cenario(("Filmes/A.Dublado.mkv", "a"), ("Filmes/B.Dublado.mkv", "b"), ("Filmes/C.Dublado.mkv", "c"));
        var (um, todos, zero) = await f.NoEscopoAsync(async sp =>
        {
            var c = new DubladosController(sp.GetRequiredService<FilmeService>());
            return (
                (List<ItemNovidade>)((OkObjectResult)await c.ObterDublados(1)).Value!,
                (List<ItemNovidade>)((OkObjectResult)await c.ObterDublados(9999)).Value!,
                (List<ItemNovidade>)((OkObjectResult)await c.ObterDublados(0)).Value!);   // 0 vira 1: o piso do clamp
        });
        Assert.Single(um);
        Assert.Equal(3, todos.Count);
        Assert.Single(zero);
    }

    // ─── esquema: coluna nova, ALTER idempotente ─────────────────────────────────────

    private static List<string> Colunas(AppDbContext db) =>
        db.Database.SqlQueryRaw<string>("""SELECT name AS "Value" FROM pragma_table_info('AnalisesDeDublagem');""").ToList();

    [Fact]
    public async Task Banco_que_ja_tinha_a_tabela_ganha_a_coluna_uma_vez_so_e_as_linhas_antigas_ficam_nulas()
    {
        var arq = Path.Combine(TestesComuns.CriarPastaTemp(), "antigo.db");
        var cs = $"Data Source={arq}";
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Filmes" ("Id" INTEGER PRIMARY KEY, "Titulo" TEXT NOT NULL, "ArquivoPath" TEXT NULL, "Assistido" INTEGER NOT NULL, "DataAdicionado" TEXT NOT NULL);
                INSERT INTO "Filmes" VALUES (1, 'x', 'a.mkv', 0, '2026-01-01');
                CREATE TABLE "AnalisesDeDublagem" (
                    "FilmeId" INTEGER NOT NULL CONSTRAINT "PK_AnalisesDeDublagem" PRIMARY KEY,
                    "DubladoIa" INTEGER NOT NULL,
                    "ArquivoBytes" INTEGER NOT NULL,
                    "AnalisadoEm" TEXT NOT NULL,
                    CONSTRAINT "FK_AnalisesDeDublagem_Filmes_FilmeId" FOREIGN KEY ("FilmeId") REFERENCES "Filmes" ("Id") ON DELETE CASCADE
                );
                INSERT INTO "AnalisesDeDublagem" VALUES (1, 1, 123, '2026-01-02');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).Options);
        Assert.DoesNotContain("AudioPtNaoIa", Colunas(db));
        DbInitializer.Executar(db);
        DbInitializer.Executar(db);   // idempotente: a segunda rodada não tenta adicionar de novo
        DbInitializer.Executar(db);

        Assert.Single(Colunas(db), c => c == "AudioPtNaoIa");
        var antiga = await db.AnalisesDeDublagem.AsNoTracking().SingleAsync();
        Assert.True(antiga.DubladoIa);                  // o que já havia continua
        Assert.Equal(123, antiga.ArquivoBytes);
        Assert.Null(antiga.AudioPtNaoIa);               // e vai ser reanalisado

        db.ChangeTracker.Clear();
        var linha = await db.AnalisesDeDublagem.SingleAsync();
        linha.AudioPtNaoIa = AudioPortugues.Indefinido;
        await db.SaveChangesAsync();
        Assert.Equal(AudioPortugues.Indefinido, (await db.AnalisesDeDublagem.AsNoTracking().SingleAsync()).AudioPtNaoIa);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Banco_novo_nasce_com_a_coluna_e_a_inicializacao_repetida_nao_muda_nada()
    {
        var arq = Path.Combine(TestesComuns.CriarPastaTemp(), "novo.db");
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={arq}").Options);
        DbInitializer.Executar(db);
        var depoisDaPrimeira = Colunas(db);
        DbInitializer.Executar(db);

        Assert.Single(depoisDaPrimeira, c => c == "AudioPtNaoIa");
        Assert.Equal(depoisDaPrimeira, Colunas(db));
        db.Filmes.Add(new Filme { Titulo = "x", ArquivoPath = "a.mkv" });
        await db.SaveChangesAsync();
        db.AnalisesDeDublagem.Add(new AnaliseDeDublagem { FilmeId = 1, AudioPtNaoIa = AudioPortugues.Sim, ArquivoBytes = 1 });
        await db.SaveChangesAsync();
        Assert.Equal(AudioPortugues.Sim, (await db.AnalisesDeDublagem.AsNoTracking().SingleAsync()).AudioPtNaoIa);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
