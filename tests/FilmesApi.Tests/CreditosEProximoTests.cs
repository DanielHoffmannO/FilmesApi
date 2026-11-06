using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

public class ProximoManualTests
{
    [Theory]
    [InlineData(300, 1300, null, false, 180, false)]     // minuto 5: só toca
    [InlineData(1150, 1300, null, false, 180, true)]     // restam 150 s
    [InlineData(1120, 1300, null, false, 180, true)]     // restam exatamente 180 s
    [InlineData(1119, 1300, null, false, 180, false)]    // restam 181 s
    [InlineData(1200, 1300, 1190.0, false, 10, true)]    // nos créditos conhecidos (restam 100, mas o limite é 10)
    [InlineData(1100, 1300, 1190.0, false, 10, false)]   // antes dos créditos
    [InlineData(300, 1300, null, true, 180, true)]       // a TV já está oferecendo o próximo
    [InlineData(300, 0, null, false, 180, false)]        // duração desconhecida: não presume
    public void Ja_acabou(double pos, double dur, double? creditos, bool oferta, double restante, bool esperado) =>
        Assert.Equal(esperado, ProximoManual.JaAcabou(pos, dur, creditos, oferta, restante));
}

/// <summary>Créditos (capítulo ou áudio dos últimos 300 s) e o "próximo episódio" que aprende. ffmpeg falso: "&lt;arquivo&gt;.fim.fp" quando o pedido é -sseof, senão "&lt;arquivo&gt;.fp".</summary>
public class CreditosEProximoTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];
    public void Dispose() { foreach (var f in _fixtures) f.Dispose(); Directory.Delete(_bin, recursive: true); }

    private static int[] Aleatorio(int n, int semente) { var r = new Random(semente); return Enumerable.Range(0, n).Select(_ => r.Next(int.MinValue, int.MaxValue)).ToArray(); }
    private static int Itens(double s) => (int)(s / DetectorDeAbertura.SegundosPorItem);
    private static void Gravar(string arq, string sufixo, int[] itens) => File.WriteAllBytes(arq + sufixo, ArmazemDeImpressoes.Codificar(itens));

    private static readonly string[] Eps = ["Serie/Serie.S02E01.mkv", "Serie/Serie.S02E02.mkv", "Serie/Serie.S02E03.mkv"];

    private string Ffmpeg() => TestesComuns.ScriptFalso(_bin, "ffmpeg.sh",
        "fim=0\nwhile [ $# -gt 0 ]; do [ \"$1\" = \"-i\" ] && arq=\"$2\"; [ \"$1\" = \"-sseof\" ] && fim=1; shift; done\n" +
        "if [ $fim = 1 ]; then f=\"$arq.fim.fp\"; else f=\"$arq.fp\"; fi\n[ -f \"$f\" ] || exit 1\ncat \"$f\"");

    private string Ffprobe(double duracao, string capitulos = "") => TestesComuns.ScriptFalso(_bin, $"ffprobe-{duracao}-{capitulos.Length}.sh",
        "echo '{\"streams\":[],\"format\":{\"duration\":\"" + duracao.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"},\"chapters\":[" + capitulos + "]}'");

    private async Task<(ScanFixture F, AnaliseDeAberturaService Analise, AutomacaoDoPlayer Auto, PlayerStateService Estado, PreferenciasTemporadaService Temp)> Cenario(
        double duracao, string capitulos = "", params string[] arquivos)
    {
        var f = new ScanFixture(ffmpeg: Ffmpeg(), ffprobe: Ffprobe(duracao, capitulos));
        _fixtures.Add(f);
        foreach (var e in arquivos.Length > 0 ? arquivos : Eps) f.Arquivo(e, new string('x', 2048));
        await f.ScanAsync();
        var cfg = new ConfigurationBuilder().Build();
        var estado = new PlayerStateService();
        var temp = new PreferenciasTemporadaService(f.Escopos);
        return (f, new AnaliseDeAberturaService(f.Escopos, f.Servico<FfmpegOptions>(), cfg, NullLogger<AnaliseDeAberturaService>.Instance),
            new AutomacaoDoPlayer(f.Escopos, estado, temp, f.Servico<FfmpegOptions>(), cfg, NullLogger<AutomacaoDoPlayer>.Instance), estado, temp);
    }

    private static Task<Dictionary<int, CreditosEpisodio>> Linhas(ScanFixture f) =>
        f.NoEscopoAsync(async sp => (await sp.GetRequiredService<AppDbContext>().Creditos.AsNoTracking().ToListAsync()).ToDictionary(c => c.FilmeId));

    [Fact]
    public async Task Capitulo_Credits_do_arquivo_marca_o_inicio_dos_creditos()
    {
        var (f, analise, _, _, _) = await Cenario(1420, "{\"start_time\":\"1290.5\",\"end_time\":\"1420.0\",\"tags\":{\"title\":\"Credits\"}}");

        await analise.ExecutarUmaVezAsync();

        var l = await Linhas(f);
        Assert.Equal(3, l.Count);
        Assert.All(l.Values, c => Assert.Equal((1290.5, OrigemAbertura.Capitulo, AnaliseDeAberturaService.VersaoAlgoritmo), (c.InicioSegundos, c.Origem, c.Versao)));
        var id = await f.IdDeAsync(Eps[0]);
        Assert.Equal(1290.5, await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ObterCreditosAsync(id)));
    }

    [Fact]
    public async Task Creditos_pelo_audio_dos_ultimos_300_s_comecam_onde_o_trecho_comum_comeca()
    {
        var (f, analise, _, _, _) = await Cenario(1300);   // a janela do fim começa em 1000 s
        var creditos = Aleatorio(Itens(60), 1);
        var antes = new[] { 100, 140, 60 };   // os créditos começam 100/140/60 s dentro da janela
        for (var i = 0; i < Eps.Length; i++)
        {
            var arq = Path.Combine(f.Media, Eps[i]);
            Gravar(arq, ".fim.fp", Aleatorio(Itens(antes[i]), 10 + i).Concat(creditos).Concat(Aleatorio(Itens(100), 20 + i)).ToArray());
            Gravar(arq, ".fp", Aleatorio(Itens(300), 30 + i));   // o começo: nada em comum (sem abertura)
        }

        await analise.ExecutarUmaVezAsync();

        var l = await Linhas(f);
        for (var i = 0; i < Eps.Length; i++)
        {
            var c = l[await f.IdDeAsync(Eps[i])];
            Assert.Equal(OrigemAbertura.Auto, c.Origem);
            Assert.InRange(c.InicioSegundos!.Value, 1000 + antes[i] - 6, 1000 + antes[i] + 1);
        }
    }

    [Fact]
    public async Task Trecho_comum_no_meio_do_episodio_nao_vale_como_creditos()
    {
        var (f, analise, _, _, _) = await Cenario(400);   // janela do fim = 100–400 s; metade do episódio = 200 s
        var comum = Aleatorio(Itens(40), 1);
        foreach (var e in Eps)
        {
            var arq = Path.Combine(f.Media, e);
            Gravar(arq, ".fim.fp", Aleatorio(Itens(10), 5).Concat(comum).Concat(Aleatorio(Itens(200), 6)).ToArray());   // "créditos" aos 110 s < 200 s
            Gravar(arq, ".fp", Aleatorio(Itens(300), 7));
        }

        await analise.ExecutarUmaVezAsync();

        Assert.All((await Linhas(f)).Values, c => Assert.Null(c.InicioSegundos));
    }

    [Fact]
    public async Task Sem_ffprobe_os_creditos_ficam_pra_proxima_rodada()
    {
        var f = new ScanFixture(ffmpeg: Ffmpeg());   // ffprobe inexistente
        _fixtures.Add(f);
        foreach (var e in Eps) f.Arquivo(e, new string('x', 2048));
        await f.ScanAsync();
        var analise = new AnaliseDeAberturaService(f.Escopos, f.Servico<FfmpegOptions>(), new ConfigurationBuilder().Build(), NullLogger<AnaliseDeAberturaService>.Instance);
        await analise.ExecutarUmaVezAsync();
        Assert.Empty(await Linhas(f));
    }

    // ─── próximo manual ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Proximo_apertado_no_fim_toca_o_proximo_e_liga_o_automatico_da_temporada()
    {
        var (f, _, auto, estado, temp) = await Cenario(1300);
        var id1 = await f.IdDeAsync(Eps[0]); var id2 = await f.IdDeAsync(Eps[1]);
        estado.Selecionar(id1);
        estado.ReportarPosicao(1200, 1300);   // restam 100 s

        var (proximo, aprendeu) = await auto.ProximoManualAsync();

        Assert.Equal((id2, true), (proximo, aprendeu));
        Assert.Equal(id2, estado.FilmeId);
        Assert.True(temp.Da("serie", 2).ProximoAutomatico);
        Assert.False(temp.Da("serie", 2).PularAbertura);
        dynamic s = estado.Snapshot();
        Assert.Equal(("proximo", "desligar-proximo"), (s.aviso.tipo, s.aviso.acao));
    }

    [Fact]
    public async Task Proximo_apertado_no_minuto_5_so_toca_nao_aprende()
    {
        var (f, _, auto, estado, temp) = await Cenario(1300);
        var id1 = await f.IdDeAsync(Eps[0]); var id2 = await f.IdDeAsync(Eps[1]);
        estado.Selecionar(id1);
        estado.ReportarPosicao(300, 1300);

        var (proximo, aprendeu) = await auto.ProximoManualAsync();

        Assert.Equal((id2, false), (proximo, aprendeu));
        Assert.False(temp.Da("serie", 2).ProximoAutomatico);
        dynamic s = estado.Snapshot();
        Assert.Null(s.aviso);
    }

    [Fact]
    public async Task Proximo_com_oferta_da_TV_no_ar_conta_como_fim_mesmo_com_duracao_desconhecida()
    {
        var (f, _, auto, estado, temp) = await Cenario(1300);
        estado.Selecionar(await f.IdDeAsync(Eps[0]));
        estado.OferecerProximo(await f.IdDeAsync(Eps[1]), "S02E02");

        var (_, aprendeu) = await auto.ProximoManualAsync();

        Assert.True(aprendeu);
        Assert.True(temp.Da("serie", 2).ProximoAutomatico);
    }

    [Fact]
    public async Task Proximo_nos_creditos_conhecidos_aprende_e_no_ultimo_episodio_nao_ha_proximo()
    {
        var (f, _, auto, estado, temp) = await Cenario(1300);
        var id1 = await f.IdDeAsync(Eps[0]);
        estado.Selecionar(id1);
        estado.DefinirContexto(id1, new ContextoDoFilme(null, null, false, false, 1000, "serie", 2));
        estado.ReportarPosicao(1010, 0);   // sem duração, mas já nos créditos

        Assert.True((await auto.ProximoManualAsync()).Aprendeu);

        estado.Selecionar(await f.IdDeAsync(Eps[2]));   // o último
        Assert.Equal((null, false), await auto.ProximoManualAsync());
        Assert.True(temp.Da("serie", 2).ProximoAutomatico);   // nada desliga sozinho
    }

    [Fact]
    public async Task Contexto_leva_o_inicio_dos_creditos_e_a_contagem_pro_snapshot()
    {
        var (f, _, auto, estado, _) = await Cenario(1300);
        var id = await f.IdDeAsync(Eps[0]);
        await f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Creditos.Add(new CreditosEpisodio { FilmeId = id, InicioSegundos = 1210, Versao = 2 });
            await db.SaveChangesAsync();
            return 0;
        });
        estado.Selecionar(id);
        await auto.AtualizarContextoAsync(id);

        dynamic s = estado.Snapshot();
        Assert.Equal(1210.0, s.inicioCreditos);
        Assert.Equal(5, s.proximoContagem);
        Assert.False(s.proximoCancelado);

        estado.CancelarProximoAutomatico();
        dynamic c = estado.Snapshot();
        Assert.True(c.proximoCancelado);
        estado.Selecionar(id);   // um episódio novo zera o cancelamento
        dynamic n = estado.Snapshot();
        Assert.False(n.proximoCancelado);
    }

    [Fact]
    public void Precisa_analisar_creditos_segue_a_mesma_regra_de_versao_e_origem()
    {
        FilmeResponse Ep(DateTime d) => new(1, "t", null, "x", false, d, null, null);
        CreditosEpisodio L(string origem, int versao, double? ini = 1) => new() { Origem = origem, Versao = versao, InicioSegundos = ini, AnalisadoEm = DateTime.UtcNow };
        var eps = new[] { Ep(DateTime.UtcNow.AddDays(-1)) };
        const int V = AnaliseDeAberturaService.VersaoAlgoritmo;

        Assert.True(AnaliseDeAberturaService.PrecisaAnalisarCreditos(null, eps));
        Assert.True(AnaliseDeAberturaService.PrecisaAnalisarCreditos(L("auto", 1), eps));
        Assert.False(AnaliseDeAberturaService.PrecisaAnalisarCreditos(L("auto", V), eps));
        Assert.False(AnaliseDeAberturaService.PrecisaAnalisarCreditos(L("capitulo", 1), eps));
        Assert.False(AnaliseDeAberturaService.PrecisaAnalisarCreditos(L("pulo", 1), eps));
    }
}
