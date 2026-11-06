using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>A análise inteira com ffmpeg FALSO: o script devolve o conteúdo de "&lt;arquivo&gt;.fp" (a impressão de áudio que o teste gravou).</summary>
public class AnaliseDeAberturaTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    private string FfmpegDasImpressoes() => TestesComuns.ScriptFalso(_bin, "ffmpeg-fp.sh", """
        while [ $# -gt 0 ]; do [ "$1" = "-i" ] && arq="$2"; shift; done
        [ -f "$arq.fp" ] || exit 1
        cat "$arq.fp"
        """);

    private static int[] Aleatorio(int n, int semente) { var r = new Random(semente); return Enumerable.Range(0, n).Select(_ => r.Next(int.MinValue, int.MaxValue)).ToArray(); }
    private static int Itens(double s) => (int)(s / DetectorDeAbertura.SegundosPorItem);

    private static void GravarImpressao(string arquivo, int[] itens) =>
        File.WriteAllBytes(arquivo + ".fp", itens.SelectMany(BitConverter.GetBytes).ToArray());

    private string FfprobeComCapitulos(string capitulosJson) => TestesComuns.ScriptFalso(_bin, "ffprobe-cap.sh",
        "echo '{\"streams\":[],\"format\":{\"duration\":\"1300.0\"},\"chapters\":[" + capitulosJson + "]}'");

    private Task<(ScanFixture F, AnaliseDeAberturaService Servico, RegistroDeTarefas Tarefas)> Cenario(params string[] episodios) =>
        CenarioComProbe(null, episodios);

    private async Task<(ScanFixture F, AnaliseDeAberturaService Servico, RegistroDeTarefas Tarefas)> CenarioComProbe(string? ffprobe, params string[] episodios)
    {
        var f = new ScanFixture(ffmpeg: FfmpegDasImpressoes(), ffprobe: ffprobe);
        _fixtures.Add(f);
        foreach (var e in episodios) f.Arquivo(e, new string('x', 2048));
        await f.ScanAsync();
        var tarefas = new RegistroDeTarefas();
        return (f, new AnaliseDeAberturaService(f.Escopos, f.Servico<FfmpegOptions>(), new ConfigurationBuilder().Build(),
            NullLogger<AnaliseDeAberturaService>.Instance, tarefas), tarefas);
    }

    private static Task<List<AberturaEpisodio>> Linhas(ScanFixture f) =>
        f.NoEscopoAsync(sp => sp.GetRequiredService<AppDbContext>().Aberturas.AsNoTracking().OrderBy(a => a.FilmeId).ToListAsync());

    private static readonly string[] Eps =
        ["Hora de Aventura/Hora.de.Aventura.S01E01.mkv", "Hora de Aventura/Hora.de.Aventura.S01E02.mkv", "Hora de Aventura/Hora.de.Aventura.S01E03.mkv"];

    [Fact]
    public async Task Acha_e_grava_a_abertura_de_cada_episodio()
    {
        var (f, servico, tarefas) = await Cenario(Eps);
        var abertura = Aleatorio(Itens(60), 1);
        var inicios = new[] { 10, 30, 20 };
        for (var i = 0; i < Eps.Length; i++)
            GravarImpressao(Path.Combine(f.Media, Eps[i]),
                Aleatorio(Itens(inicios[i]), 10 + i).Concat(abertura).Concat(Aleatorio(Itens(200), 20 + i)).ToArray());

        Assert.Equal(3, await servico.ExecutarUmaVezAsync());

        var linhas = (await Linhas(f)).ToDictionary(a => a.FilmeId);
        Assert.Equal(3, linhas.Count);
        for (var i = 0; i < 3; i++)
        {
            var l = linhas[await f.IdDeAsync(Eps[i])];
            Assert.InRange(l.InicioSegundos!.Value, inicios[i] - 0.5, inicios[i] + 0.5);
            Assert.InRange(l.FimSegundos!.Value, inicios[i] + 59, inicios[i] + 61);
        }
        Assert.Equal("ok", Assert.Single(tarefas.Snapshot()).Estado);
        var id = await f.IdDeAsync(Eps[1]);
        var (ini, fim) = (await f.NoEscopoAsync(async sp => await sp.GetRequiredService<FilmeService>().ObterAberturaAsync(id)))!.Value;
        Assert.InRange(ini, 29.5, 30.5);
        Assert.InRange(fim, 89, 91);
    }

    private static int[] ComAbertura(int[] abertura, int antes, int semente) =>
        Aleatorio(Itens(antes), semente).Concat(abertura).Concat(Aleatorio(Itens(200), semente + 100)).ToArray();

    private const string Opening2a90 = "{\"start_time\":\"2.0\",\"end_time\":\"90.0\",\"tags\":{\"title\":\"Opening\"}}";
    private const string Opening2s = "{\"start_time\":\"10.0\",\"end_time\":\"12.3\",\"tags\":{\"title\":\"Opening\"}}";

    [Fact]
    public async Task Resumo_da_tarefa_mostra_o_progresso_real_e_o_motivo_da_pausa()
    {
        var (f, servico, tarefas) = await Cenario(Eps);
        await servico.ExecutarUmaVezAsync();   // sem .fp: nada é gravado, tudo continua pendente
        var resumo = tarefas.Snapshot().Single(t => t.Nome == "abertura").Resumo;
        Assert.Equal($"v{AnaliseDeAberturaService.VersaoAlgoritmo}: 0 de 3 episódios; 1 temporada(s) pendente(s)", resumo);

        Assert.Equal("3 analisado(s) agora · v2: 240 de 1149 episódios; 46 temporada(s) pendente(s) · pausado: servidor ocupado (transcode/remux ativo)",
            AnaliseDeAberturaService.Resumo(3, 46, (240, 1149), "servidor ocupado (transcode/remux ativo)"));
        Assert.Equal("v2: 5 de 5 episódios; 0 temporada(s) pendente(s)", AnaliseDeAberturaService.Resumo(0, 0, (5, 5), null));
    }

    [Fact]
    public async Task Episodio_sem_arquivo_nao_impede_os_seguintes_de_usar_o_capitulo()
    {
        string[] eps = [.. Eps, "Hora de Aventura/Hora.de.Aventura.S01E04.mkv"];
        var (f, servico, _) = await CenarioComProbe(FfprobeComCapitulos(Opening2a90), eps);
        var id3 = await f.IdDeAsync(eps[2]);
        File.Delete(Path.Combine(f.Media, eps[2]));   // o 3º some do disco depois do scan: ResolverCaminho devolve null
        var abertura = Aleatorio(Itens(60), 1);
        foreach (var e in new[] { eps[0], eps[1], eps[3] }) GravarImpressao(Path.Combine(f.Media, e), ComAbertura(abertura, 20, 10));

        await servico.ExecutarUmaVezAsync();

        var linhas = (await Linhas(f)).ToDictionary(a => a.FilmeId);
        var id4 = await f.IdDeAsync(eps[3]);
        Assert.False(linhas.ContainsKey(id3));
        Assert.Equal(OrigemMarca.Capitulo, linhas[id4].Origem);   // antes: o `break` no 3º episódio deixava o 4º pra comparação de áudio
        Assert.Equal((2.0, 90.0), (linhas[id4].InicioSegundos, linhas[id4].FimSegundos));
    }

    [Fact]
    public async Task Capitulo_Opening_do_arquivo_vence_a_comparacao_de_audio_e_o_audio_nao_sobrescreve()
    {
        var (f, servico, _) = await CenarioComProbe(FfprobeComCapitulos(Opening2a90), Eps);
        var abertura = Aleatorio(Itens(60), 1);
        for (var i = 0; i < Eps.Length; i++) GravarImpressao(Path.Combine(f.Media, Eps[i]), ComAbertura(abertura, 20 + i * 5, 10 + i));

        Assert.Equal(3, await servico.ExecutarUmaVezAsync());
        var linhas = await Linhas(f);
        Assert.All(linhas, a => Assert.Equal((OrigemMarca.Capitulo, 2.0, 90.0, AnaliseDeAberturaService.VersaoAlgoritmo), (a.Origem, a.InicioSegundos, a.FimSegundos, a.Versao)));
        Assert.Equal(0, await servico.ExecutarUmaVezAsync());   // 'capitulo' nunca é refeita
    }

    [Fact]
    public async Task Opening_de_2_segundos_nao_e_abertura_e_cai_na_comparacao_de_audio()
    {
        var (f, servico, _) = await CenarioComProbe(FfprobeComCapitulos(Opening2s), Eps);
        var abertura = Aleatorio(Itens(40), 1);
        for (var i = 0; i < Eps.Length; i++) GravarImpressao(Path.Combine(f.Media, Eps[i]), ComAbertura(abertura, 20 + i * 5, 10 + i));

        await servico.ExecutarUmaVezAsync();
        Assert.All(await Linhas(f), a => Assert.Equal(OrigemMarca.Auto, a.Origem));
    }

    [Fact]
    public async Task Linha_pulo_fica_intacta_e_linha_auto_antiga_e_reanalisada_sem_ler_o_disco()
    {
        var (f, servico, _) = await CenarioComProbe(null, Eps);
        var abertura = Aleatorio(Itens(60), 1);
        for (var i = 0; i < Eps.Length; i++) GravarImpressao(Path.Combine(f.Media, Eps[i]), ComAbertura(abertura, 20 + i * 5, 10 + i));
        await servico.ExecutarUmaVezAsync();   // gera e guarda as impressões

        var ids = new[] { await f.IdDeAsync(Eps[0]), await f.IdDeAsync(Eps[1]), await f.IdDeAsync(Eps[2]) };
        await f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var a0 = await db.Aberturas.FindAsync(ids[0]); a0!.Origem = OrigemMarca.Pulo; a0.InicioSegundos = 1; a0.FimSegundos = 2; a0.Versao = 1;
            var a1 = await db.Aberturas.FindAsync(ids[1]); a1!.Versao = 1; a1.InicioSegundos = 5; a1.FimSegundos = 6;   // marcação de um algoritmo antigo
            var a2 = await db.Aberturas.FindAsync(ids[2]); a2!.Versao = 1;
            await db.SaveChangesAsync();
            return 0;
        });
        foreach (var e in Eps) File.Delete(Path.Combine(f.Media, e) + ".fp");   // o ffmpeg falso passa a falhar: só as impressões guardadas servem

        Assert.Equal(2, await servico.ExecutarUmaVezAsync());   // as duas 'auto'; a 'pulo' não conta nem muda

        var l = (await Linhas(f)).ToDictionary(a => a.FilmeId);
        Assert.Equal((OrigemMarca.Pulo, 1.0, 2.0), (l[ids[0]].Origem, l[ids[0]].InicioSegundos, l[ids[0]].FimSegundos));
        Assert.NotEqual(5.0, l[ids[1]].InicioSegundos);   // resultado novo trocou a marcação antiga
        Assert.Equal(AnaliseDeAberturaService.VersaoAlgoritmo, l[ids[1]].Versao);
    }

    [Fact]
    public async Task Reanalise_que_nao_acha_nada_nao_apaga_a_marcacao_antiga_mas_marca_a_versao()
    {
        var (f, servico, _) = await CenarioComProbe(null, Eps);
        for (var i = 0; i < Eps.Length; i++) GravarImpressao(Path.Combine(f.Media, Eps[i]), Aleatorio(Itens(300), i));   // nada em comum
        await servico.ExecutarUmaVezAsync();
        var id = await f.IdDeAsync(Eps[1]);
        await f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var a = await db.Aberturas.FindAsync(id); a!.Versao = 1; a.InicioSegundos = 5; a.FimSegundos = 50;
            await db.SaveChangesAsync();
            return 0;
        });

        await servico.ExecutarUmaVezAsync();

        var depois = (await Linhas(f)).Single(a => a.FilmeId == id);
        Assert.Equal((5.0, 50.0, AnaliseDeAberturaService.VersaoAlgoritmo), (depois.InicioSegundos, depois.FimSegundos, depois.Versao));
    }

    [Fact]
    public void Vizinhos_mais_2_so_quando_mais_1_nao_achou()
    {
        var abertura = Aleatorio(Itens(40), 1);
        var comAbertura = ComAbertura(abertura, 20, 10);
        var outra = ComAbertura(abertura, 35, 11);
        var semAbertura = Aleatorio(Itens(300), 5);

        var t = AnaliseDeAberturaService.MelhorTrecho(0, [comAbertura, semAbertura, outra], 10);   // +1 não tem; +2 tem
        Assert.NotNull(t);
        Assert.InRange(t!.InicioB, 34, 36);

        Assert.Null(AnaliseDeAberturaService.MelhorTrecho(0, [comAbertura, semAbertura, Aleatorio(Itens(300), 6)], 10));
    }

    [Theory]
    [InlineData("auto", "auto", true)] [InlineData("auto", "capitulo", true)] [InlineData("auto", "pulo", true)]
    [InlineData("capitulo", "auto", false)] [InlineData("capitulo", "capitulo", true)] [InlineData("capitulo", "pulo", true)]
    [InlineData("pulo", "auto", false)] [InlineData("pulo", "capitulo", false)] [InlineData("pulo", "pulo", true)]
    public void Precedencia_das_origens(string atual, string nova, bool pode) =>
        Assert.Equal(pode, OrigemMarca.PodeSobrescrever(atual, nova));

    [Fact]
    public void Precisa_analisar_so_sem_linha_ou_auto_antiga_ou_nao_achou_com_episodio_novo()
    {
        var ontem = DateTime.UtcNow.AddDays(-1);
        FilmeResponse Ep(DateTime adicionado) => new(1, "t", null, "x", false, adicionado, null, null);
        AberturaEpisodio L(string origem, int versao, double? ini = 1) => new() { Origem = origem, Versao = versao, InicioSegundos = ini, AnalisadoEm = DateTime.UtcNow };
        var eps = new[] { Ep(ontem) };
        const int V = AnaliseDeAberturaService.VersaoAlgoritmo;

        Assert.True(AnaliseDeAberturaService.PrecisaAnalisar(null, eps));
        Assert.True(AnaliseDeAberturaService.PrecisaAnalisar(L("auto", 1), eps));
        Assert.False(AnaliseDeAberturaService.PrecisaAnalisar(L("auto", V), eps));
        Assert.False(AnaliseDeAberturaService.PrecisaAnalisar(L("pulo", 1), eps));
        Assert.False(AnaliseDeAberturaService.PrecisaAnalisar(L("capitulo", 1), eps));
        Assert.False(AnaliseDeAberturaService.PrecisaAnalisar(L("auto", V, null), eps));
        Assert.True(AnaliseDeAberturaService.PrecisaAnalisar(L("auto", V, null), [Ep(DateTime.UtcNow.AddMinutes(1))]));
    }

    [Fact]
    public void Impressao_vira_bytes_e_volta_igual()
    {
        var itens = new[] { 0, 1, -1, int.MaxValue, int.MinValue, 123456789 };
        Assert.Equal(itens, ArmazemDeImpressoes.Decodificar(ArmazemDeImpressoes.Codificar(itens)));
    }

    [Fact]
    public async Task Banco_antigo_ganha_versao_e_origem_nas_aberturas_com_os_padroes()
    {
        var arq = Path.Combine(TestesComuns.CriarPastaTemp(), "velho.db");
        var cs = $"Data Source={arq}";
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Filmes" ("Id" INTEGER PRIMARY KEY, "Titulo" TEXT NOT NULL, "ArquivoPath" TEXT NULL, "Assistido" INTEGER NOT NULL, "DataAdicionado" TEXT NOT NULL);
                CREATE TABLE "Aberturas" ("FilmeId" INTEGER NOT NULL PRIMARY KEY, "InicioSegundos" REAL NULL, "FimSegundos" REAL NULL, "AnalisadoEm" TEXT NOT NULL);
                INSERT INTO "Filmes" VALUES (1, 'x', 'a.mkv', 0, '2026-01-01');
                INSERT INTO "Aberturas" VALUES (1, 10.0, 90.0, '2026-01-02');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).Options);
        DbInitializer.Executar(db);
        DbInitializer.Executar(db);

        var a = await db.Aberturas.AsNoTracking().SingleAsync();
        Assert.Equal((10.0, 90.0, 1, "auto"), (a.InicioSegundos, a.FimSegundos, a.Versao, a.Origem));
        Assert.Equal(0, await db.Impressoes.CountAsync());
        Assert.Equal(0, await db.Creditos.CountAsync());   // tabela nova também nasce no banco antigo
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Sem_abertura_em_comum_marca_como_analisado_e_nao_repete()
    {
        var (f, servico, _) = await Cenario(Eps);
        for (var i = 0; i < Eps.Length; i++) GravarImpressao(Path.Combine(f.Media, Eps[i]), Aleatorio(Itens(300), i));

        Assert.Equal(3, await servico.ExecutarUmaVezAsync());
        Assert.All(await Linhas(f), a => Assert.Null(a.InicioSegundos));

        Assert.Equal(0, await servico.ExecutarUmaVezAsync());   // nada novo na temporada: não gasta ffmpeg de novo
    }

    [Fact]
    public async Task Ffmpeg_sem_saida_nao_grava_nada_para_tentar_de_novo()
    {
        var (f, servico, _) = await Cenario(Eps);   // nenhum .fp: o ffmpeg falso sai com erro

        Assert.Equal(0, await servico.ExecutarUmaVezAsync());
        Assert.Empty(await Linhas(f));
    }

    [Fact]
    public async Task Temporada_de_um_episodio_so_nao_e_analisada()
    {
        var (f, servico, _) = await Cenario(Eps[0]);
        GravarImpressao(Path.Combine(f.Media, Eps[0]), Aleatorio(Itens(300), 1));

        Assert.Equal(0, await servico.ExecutarUmaVezAsync());
    }

    [Fact]
    public async Task Intervalo_zero_declara_a_tarefa_desligada()
    {
        var tarefas = new RegistroDeTarefas();
        var servico = new AnaliseDeAberturaService(null!, new FfmpegOptions("x", "y"),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AberturaIntervaloMinutos"] = "0" }).Build(),
            NullLogger<AnaliseDeAberturaService>.Instance, tarefas);
        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!;
        Assert.Equal("desligada", Assert.Single(tarefas.Snapshot()).Estado);
    }
}

public class ImpressaoDeAudioTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    public void Dispose() => Directory.Delete(_bin, recursive: true);

    [Fact]
    public async Task Le_os_inteiros_little_endian_do_stdout_do_ffmpeg()
    {
        var ff = TestesComuns.ScriptFalso(_bin, "ff.sh", @"printf '\001\000\000\000\377\377\377\377'");
        var itens = await ImpressaoDeAudio.ObterAsync(ff, "x.mkv", default);
        Assert.Equal(new[] { 1, -1 }, itens);
    }

    [Fact]
    public async Task Falha_do_ffmpeg_ou_binario_inexistente_vira_null()
    {
        Assert.Null(await ImpressaoDeAudio.ObterAsync(TestesComuns.ScriptFalso(_bin, "ruim.sh", "exit 1"), "x.mkv", default));
        Assert.Null(await ImpressaoDeAudio.ObterAsync("/nao/existe/ffmpeg", "x.mkv", default));
    }
}

/// <summary>Ponta a ponta com o ffmpeg DE VERDADE: gera dois "episódios" de ruído com a mesma abertura em pontos diferentes, tira a
/// impressão e acha o trecho. Só roda onde o ffmpeg do PATH tem o muxer chromaprint (a máquina de dev; o jellyfin-ffmpeg da imagem tem).</summary>
public class AberturaComFfmpegRealTests : IDisposable
{
    private readonly string _pasta = TestesComuns.CriarPastaTemp();
    public void Dispose() => Directory.Delete(_pasta, recursive: true);

    private static async Task<(int Codigo, string Saida)> Rodar(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            var saida = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, saida);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }
    }

    private async Task<string> Episodio(string nome, int cenaAntes, int semente1, int semente2)
    {
        var arq = Path.Combine(_pasta, nome);
        var (cod, _) = await Rodar("-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"anoisesrc=d={cenaAntes}:seed={semente1}:r=44100",
            "-f", "lavfi", "-i", "anoisesrc=d=40:seed=99:r=44100",
            "-f", "lavfi", "-i", $"anoisesrc=d=100:seed={semente2}:r=44100",
            "-filter_complex", "[0][1][2]concat=n=3:v=0:a=1", "-c:a", "aac", "-b:a", "96k", arq);
        Assert.Equal(0, cod);
        return arq;
    }

    [Fact]
    public async Task Acha_a_abertura_comum_em_audio_real()
    {
        var (_, muxers) = await Rodar("-hide_banner", "-muxers");
        if (!muxers.Contains("chromaprint")) return;   // este ffmpeg não tem chromaprint: nada a conferir aqui

        var a = await Episodio("a.m4a", 10, 1, 2);
        var b = await Episodio("b.m4a", 25, 3, 4);
        var fa = await ImpressaoDeAudio.ObterAsync("ffmpeg", a, default);
        var fb = await ImpressaoDeAudio.ObterAsync("ffmpeg", b, default);
        Assert.NotNull(fa); Assert.NotNull(fb);

        var t = DetectorDeAbertura.Encontrar(fa!, fb!);

        Assert.NotNull(t);
        // o fim é o que importa pro botão (pra onde pular); o começo do ruído sintético "vaza" um pouco antes da abertura
        Assert.InRange(t!.InicioA, 0, 11);   // ruído estacionário casa com ruído estacionário: o folgado estende até o teto de 8 s
        Assert.InRange(t.FimA, 48, 59);   // o folgado estende até 8 s além do que o estrito achou
        Assert.InRange(t.InicioB - t.InicioA, 14, 16);   // (o deslocamento entre os episódios não muda com a extensão)   // B começa a abertura 15 s depois de A
    }
}
