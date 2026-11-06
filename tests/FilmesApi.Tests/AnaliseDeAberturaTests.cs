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

    private async Task<(ScanFixture F, AnaliseDeAberturaService Servico, RegistroDeTarefas Tarefas)> Cenario(params string[] episodios)
    {
        var f = new ScanFixture(ffmpeg: FfmpegDasImpressoes());
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
        Assert.InRange(t!.InicioA, 5, 11);
        Assert.InRange(t.FimA, 48, 52);
        Assert.InRange(t.InicioB - t.InicioA, 14, 16);   // B começa a abertura 15 s depois de A
    }
}
