using System.Text;
using System.Text.Json;
using FilmesApi.Controllers;
using FilmesApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>O scan sob demanda do webhook: <see cref="ScanPeriodicoService.Solicitar"/> (rajada = 1 scan, nunca dois ao mesmo
/// tempo, funciona com o timer desligado) e o endpoint <c>POST /api/webhook/arr</c> (404 sem chave, 401 com a chave errada…).</summary>
public class WebhookScanSobDemandaTests
{
    private static IConfiguration Config(string? chave = null, string debounce = "0.2", string? intervaloMinutos = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Webhook:Chave"] = chave,
            ["Webhook:DebounceSegundos"] = debounce,
            ["Webhook:EsperaMaximaSegundos"] = "5",
            ["ScanIntervaloMinutos"] = intervaloMinutos,
        }).Build();

    private static async Task AguardarOciosoAsync(ScanPeriodicoService servico)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (!servico.Ocioso)
        {
            Assert.True(DateTime.UtcNow < limite, "o scan sob demanda não terminou em 15 s");
            await Task.Delay(20);
        }
    }

    private static int Execucoes(RegistroDeTarefas r) => r.Snapshot().SingleOrDefault(t => t.Nome == "scan")?.Execucoes ?? 0;

    [Fact]
    public async Task Rajada_de_pedidos_roda_um_scan_so_e_importa_o_arquivo()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Hora.de.Aventura.S05E05.mkv");
        var registro = new RegistroDeTarefas();
        var servico = new ScanPeriodicoService(f.Escopos, Config(), NullLogger<ScanPeriodicoService>.Instance, registro);

        for (var i = 0; i < 6; i++) servico.Solicitar();
        await AguardarOciosoAsync(servico);

        Assert.Equal(1, Execucoes(registro));
        Assert.Single(await f.CaminhosNoBancoAsync());
    }

    [Fact]
    public async Task Pedido_depois_do_scan_acabar_roda_outro_scan()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Hora.de.Aventura.S05E05.mkv");
        var registro = new RegistroDeTarefas();
        var servico = new ScanPeriodicoService(f.Escopos, Config(), NullLogger<ScanPeriodicoService>.Instance, registro);
        servico.Solicitar();
        await AguardarOciosoAsync(servico);

        f.Arquivo("Series/Hora.de.Aventura.S05E06.mkv");
        servico.Solicitar();
        await AguardarOciosoAsync(servico);

        Assert.Equal(2, Execucoes(registro));
        Assert.Equal(2, (await f.CaminhosNoBancoAsync()).Count);
    }

    [Fact]
    public async Task Funciona_com_o_scan_periodico_desligado()
    {
        // ScanIntervaloMinutos=0: o timer nem existe (o serviço termina na hora), mas o webhook continua valendo
        using var f = new ScanFixture();
        f.Arquivo("Series/Hora.de.Aventura.S05E05.mkv");
        var servico = new ScanPeriodicoService(f.Escopos, Config(intervaloMinutos: "0"), NullLogger<ScanPeriodicoService>.Instance);
        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        servico.Solicitar();
        await AguardarOciosoAsync(servico);

        Assert.Single(await f.CaminhosNoBancoAsync());
        await servico.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Parar_o_servico_cancela_o_pedido_pendente_sem_rodar_scan()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Hora.de.Aventura.S05E05.mkv");
        var registro = new RegistroDeTarefas();
        var servico = new ScanPeriodicoService(f.Escopos, Config(debounce: "30"), NullLogger<ScanPeriodicoService>.Instance, registro);
        servico.Solicitar();

        await servico.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        servico.Solicitar();   // depois de parar, não agenda nada
        await Task.Delay(100);

        Assert.Equal(0, Execucoes(registro));
        Assert.Empty(await f.CaminhosNoBancoAsync());
    }

    // ─── o endpoint ─────────────────────────────────────────────────────────────────────────────

    private static WebhookArrController Controlador(IConfiguration config, ScanPeriodicoService scan, string corpo)
    {
        var bytes = Encoding.UTF8.GetBytes(corpo);
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return new WebhookArrController(config, scan, NullLogger<WebhookArrController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }

    private const string Download = """{"eventType":"Download","series":{"id":1},"episodeFile":{"id":2}}""";

    [Fact]
    public async Task Endpoint_desligado_sem_chave_configurada_responde_404_e_nao_agenda_scan()
    {
        using var f = new ScanFixture();
        var scan = new ScanPeriodicoService(f.Escopos, Config(chave: ""), NullLogger<ScanPeriodicoService>.Instance);

        var r = await Controlador(Config(chave: ""), scan, Download).Arr("qualquer");

        Assert.IsType<NotFoundResult>(r);
        Assert.True(scan.Ocioso);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("errada")]
    public async Task Chave_ausente_ou_errada_responde_401_e_nao_agenda_scan(string? enviada)
    {
        using var f = new ScanFixture();
        var config = Config(chave: "segredo");
        var scan = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);

        var r = await Controlador(config, scan, Download).Arr(enviada);

        Assert.IsType<UnauthorizedObjectResult>(r);
        Assert.True(scan.Ocioso);
    }

    [Fact]
    public async Task Evento_Test_responde_200_ok_e_nao_agenda_scan()
    {
        using var f = new ScanFixture();
        var config = Config(chave: "segredo");
        var scan = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);

        var r = await Controlador(config, scan, """{"eventType":"Test","series":{"id":1}}""").Arr("segredo");

        var ok = Assert.IsType<OkObjectResult>(r);
        Assert.Equal("""{"ok":true}""", JsonSerializer.Serialize(ok.Value));
        Assert.True(scan.Ocioso);
    }

    [Fact]
    public async Task Importacao_responde_200_na_hora_e_deixa_o_scan_agendado_em_segundo_plano()
    {
        using var f = new ScanFixture();
        var config = Config(chave: "segredo", debounce: "30");
        var scan = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);

        var r = await Controlador(config, scan, Download).Arr("segredo");

        Assert.IsType<OkObjectResult>(r);
        Assert.False(scan.Ocioso);   // pedido esperando o debounce (o scan em si não rodou ainda)
        await scan.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Evento_que_nao_muda_o_disco_responde_200_e_e_ignorado()
    {
        using var f = new ScanFixture();
        var config = Config(chave: "segredo");
        var scan = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);

        var r = await Controlador(config, scan, """{"eventType":"Grab","series":{"id":1}}""").Arr("segredo");

        var ok = Assert.IsType<OkObjectResult>(r);
        Assert.Equal("""{"ok":true,"ignorado":true}""", JsonSerializer.Serialize(ok.Value));
        Assert.True(scan.Ocioso);
    }

    [Theory]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("""{"semEventType":1}""")]
    public async Task Corpo_que_nao_e_webhook_do_arr_responde_400(string corpo)
    {
        using var f = new ScanFixture();
        var config = Config(chave: "segredo");
        var scan = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);

        var r = await Controlador(config, scan, corpo).Arr("segredo");

        Assert.IsType<BadRequestObjectResult>(r);
        Assert.True(scan.Ocioso);
    }

    [Fact]
    public async Task Corpo_acima_do_teto_responde_413()
    {
        using var f = new ScanFixture();
        var config = Config(chave: "segredo");
        var scan = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);
        var grande = new string('x', WebhookArr.TamanhoMaximoDoCorpo + 10);

        var r = await Controlador(config, scan, grande).Arr("segredo");

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<StatusCodeResult>(r).StatusCode);
    }
}
