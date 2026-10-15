using FilmesApi.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FilmesApi.Tests;

/// <summary>O scan já segura os itens de um disco que sumiu ("montagem caída?"), mas só logava um warning — o dono
/// descobria num 404. Agora o estado vai pro /api/status e o /health vira "Degraded" (503) pro Uptime Kuma avisar.
/// Mesma regra do <see cref="FilmeService.PrefixosRemoviveis"/>, sem varrer o disco de novo.</summary>
[Collection("NomesGlobais")]
public class MontagensTests
{
    private static async Task<HealthCheckResult> Saude(EstadoMontagens estado) =>
        await new MontagensHealthCheck(estado).CheckHealthAsync(new HealthCheckContext());

    // ─── a regra, isolada ────────────────────────────────────────────────────────────

    [Fact]
    public void Prefixo_com_itens_que_nao_pode_remover_e_suspeita()
    {
        var m = EstadoMontagens.Classificar(
            new Dictionary<string, int> { ["extra"] = 40, ["Series"] = 120 },
            new Dictionary<string, int> { ["Series"] = 120 },
            new HashSet<string> { "Series" });

        Assert.Equal(
            [("extra", 40, 0, "suspeita"), ("Series", 120, 120, "ok")],
            m.Select(x => (x.Prefixo, x.ItensNoCatalogo, x.ArquivosNoDisco, x.Estado)));
    }

    [Fact]
    public void Prefixo_novo_no_disco_ainda_sem_item_aparece_como_ok()
    {
        var m = EstadoMontagens.Classificar(
            new Dictionary<string, int>(), new Dictionary<string, int> { ["Novo"] = 3 }, new HashSet<string>());

        Assert.Equal([("Novo", 0, 3, "ok")], m.Select(x => (x.Prefixo, x.ItensNoCatalogo, x.ArquivosNoDisco, x.Estado)));
    }

    [Fact]
    public void Raiz_aparece_com_nome_legivel() =>
        Assert.Equal("(raiz)", Assert.Single(EstadoMontagens.Classificar(
            new Dictionary<string, int> { [""] = 2 }, new Dictionary<string, int> { [""] = 2 }, new HashSet<string> { "" })).Prefixo);

    // ─── o health check ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Antes_do_primeiro_scan_esta_saudavel() =>
        Assert.Equal(HealthStatus.Healthy, (await Saude(new EstadoMontagens())).Status);

    [Fact]
    public async Task Com_prefixo_suspeito_fica_degradado_e_diz_qual()
    {
        var estado = new EstadoMontagens();
        estado.Atualizar([new MontagemDiag("extra", 40, 0, EstadoMontagens.Suspeita), new MontagemDiag("Series", 5, 5, EstadoMontagens.Ok)]);

        var r = await Saude(estado);

        Assert.Equal(HealthStatus.Degraded, r.Status);
        Assert.Contains("extra", r.Description);
        Assert.DoesNotContain("Series", r.Description);
    }

    // ─── de ponta a ponta, com scan de verdade ───────────────────────────────────────

    [Fact]
    public async Task Prefixo_extra_vazio_deixa_degradado()
    {
        using var f = new ScanFixture();
        f.Arquivo("Filmes/Matrix.1999.mkv");
        f.Arquivo("extra/The.Sopranos.S01E01.mkv");
        f.Arquivo("extra/The.Sopranos.S01E02.mkv");
        await f.ScanAsync();
        Assert.Equal(HealthStatus.Healthy, (await Saude(f.Montagens)).Status);

        // o HD de backup desmonta: /media/extra continua existindo, vazio
        File.Delete(Path.Combine(f.Media, "extra", "The.Sopranos.S01E01.mkv"));
        File.Delete(Path.Combine(f.Media, "extra", "The.Sopranos.S01E02.mkv"));
        await f.ScanAsync();

        Assert.Equal(HealthStatus.Degraded, (await Saude(f.Montagens)).Status);
        var extra = Assert.Single(f.Montagens.Montagens, m => m.Prefixo == "extra");
        Assert.Equal((2, 0, "suspeita"), (extra.ItensNoCatalogo, extra.ArquivosNoDisco, extra.Estado));
        Assert.Equal(("Filmes", 1, 1, "ok"), Resumo(Assert.Single(f.Montagens.Montagens, m => m.Prefixo == "Filmes")));
    }

    [Fact]
    public async Task Pasta_apagada_de_proposito_continua_saudavel()
    {
        using var f = new ScanFixture();
        f.Arquivo("Filmes/Matrix.1999.mkv");
        f.Arquivo("Filmes/Alien.1979.mkv");
        f.Arquivo("Series/Show/Show.S01E01.mkv");
        f.Arquivo("Series/Show/Show.S01E02.mkv");
        f.Arquivo("Docs/Doc.2020.mkv");
        await f.ScanAsync();

        Directory.Delete(Path.Combine(f.Media, "Series"), recursive: true);   // o dono apagou a série inteira
        var r = await f.ScanAsync();

        Assert.Equal(2, r.Removidos);
        Assert.Equal(HealthStatus.Healthy, (await Saude(f.Montagens)).Status);
        Assert.DoesNotContain(f.Montagens.Montagens, m => m.Prefixo == "Series");
        Assert.All(f.Montagens.Montagens, m => Assert.Equal("ok", m.Estado));
    }

    [Fact]
    public async Task Disco_todo_vazio_deixa_tudo_suspeito()
    {
        using var f = new ScanFixture();
        f.Arquivo("Filmes/Matrix.1999.mkv");
        f.Arquivo("extra/Show.S01E01.mkv");
        await f.ScanAsync();

        File.Delete(Path.Combine(f.Media, "Filmes", "Matrix.1999.mkv"));
        File.Delete(Path.Combine(f.Media, "extra", "Show.S01E01.mkv"));
        await f.ScanAsync();

        Assert.Equal(HealthStatus.Degraded, (await Saude(f.Montagens)).Status);
        Assert.All(f.Montagens.Montagens, m => Assert.Equal("suspeita", m.Estado));
    }

    [Fact]
    public async Task Volta_ao_normal_quando_o_disco_volta()
    {
        using var f = new ScanFixture();
        f.Arquivo("Filmes/Matrix.1999.mkv");
        var extra = f.Arquivo("extra/Show.S01E01.mkv");
        await f.ScanAsync();
        File.Delete(extra);
        await f.ScanAsync();
        Assert.Equal(HealthStatus.Degraded, (await Saude(f.Montagens)).Status);

        f.Arquivo("extra/Show.S01E01.mkv");   // o HD voltou
        await f.ScanAsync();

        Assert.Equal(HealthStatus.Healthy, (await Saude(f.Montagens)).Status);
    }

    [Fact]
    public async Task Item_ignorado_nao_conta_como_item_no_catalogo()
    {
        // um prefixo cujo único "item" era propaganda ignorada não pode ficar eternamente suspeito
        using var f = new ScanFixture();
        f.Arquivo("Filmes/Matrix.1999.mkv");
        f.Arquivo("extra/1XBET.COM_promo.mp4");
        await f.ScanAsync();   // sem "ignorar": entra no catálogo

        MediaNomeParser.DefinirNomesPersonalizados(NomesPersonalizadosService.Ler("""{ "ignorar": ["zz-nada/*"] }"""));
        try { Assert.Contains(f.Montagens.Montagens, m => m.Prefixo == "extra" && m.ItensNoCatalogo == 1); }
        finally { MediaNomeParser.DefinirNomesPersonalizados(null); }
    }

    private static (string, int, int, string) Resumo(MontagemDiag m) => (m.Prefixo, m.ItensNoCatalogo, m.ArquivosNoDisco, m.Estado);
}
