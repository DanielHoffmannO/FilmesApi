using System.Net;
using System.Text.Json;
using FilmesApi.Controllers;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

public class TvIrTests
{
    private sealed class Fabrica(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> resposta) : IHttpClientFactory
    {
        public List<(string Url, string Corpo, HttpRequestMessage Req)> Chamadas = [];
        public HttpClient CreateClient(string name) => new(new Falso(this, resposta));
        private sealed class Falso(Fabrica f, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> r) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                f.Chamadas.Add((req.RequestUri!.ToString(), await req.Content!.ReadAsStringAsync(ct), req));
                return await r(req, ct);
            }
        }
    }

    private static TvIrService Servico(Fabrica f, bool configurado = true, TimeSpan? limite = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(configurado
            ? new Dictionary<string, string?> { ["PainelIrUrl"] = "http://painel.teste:5001/", ["PainelIrId"] = "hub123" }
            : new Dictionary<string, string?>()).Build();
        return new TvIrService(f, cfg, NullLogger<TvIrService>.Instance) { Limite = limite ?? TimeSpan.FromSeconds(8) };
    }

    private static Fabrica Ok() => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

    [Theory]
    [InlineData("energia"), InlineData("vol+"), InlineData("vol-"), InlineData("canal+"), InlineData("ok"), InlineData("esquerda")]
    public async Task Tecla_valida_vai_ao_painel_com_id_da_config_X_Painel_e_sem_Origin(string tecla)
    {
        var f = Ok();
        var r = await Servico(f).EnviarAsync(tecla);

        Assert.True(r.Enviado);
        var c = Assert.Single(f.Chamadas);
        Assert.Equal("http://painel.teste:5001/api/ir", c.Url);
        Assert.Equal("1", c.Req.Headers.GetValues("X-Painel").Single());
        Assert.False(c.Req.Headers.Contains("Origin"));
        Assert.StartsWith("application/json", c.Req.Content!.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(c.Corpo);
        Assert.Equal("hub123", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(tecla, doc.RootElement.GetProperty("tecla").GetString());
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(null), InlineData(""), InlineData("ENERGIA"), InlineData("desligar"), InlineData("vol+ ")]
    public async Task Tecla_fora_da_lista_fechada_nao_chega_ao_painel(string? tecla)
    {
        var f = Ok();
        var r = await Servico(f).EnviarAsync(tecla);
        Assert.False(r.Enviado);
        Assert.Equal(400, r.Status);
        Assert.Empty(f.Chamadas);
    }

    [Fact]
    public void Lista_fechada_tem_as_14_teclas_do_painel() =>
        Assert.Equal(14, TvIrService.Teclas.Distinct().Count());

    [Fact]
    public async Task Sem_configuracao_responde_503_amigavel_sem_chamar()
    {
        var f = Ok();
        var r = await Servico(f, configurado: false).EnviarAsync("mudo");
        Assert.Equal(503, r.Status);
        Assert.Contains("não está configurado", r.Mensagem);
        Assert.Empty(f.Chamadas);
    }

    [Fact]
    public async Task Hub_fora_da_rede_demora_e_vira_504_em_portugues()
    {
        var f = new Fabrica(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var r = await Servico(f, limite: TimeSpan.FromMilliseconds(150)).EnviarAsync("mudo");
        Assert.False(r.Enviado);
        Assert.Equal(504, r.Status);
        Assert.Contains("hub", r.Mensagem);
    }

    [Fact]
    public async Task Painel_inacessivel_vira_502_e_recusa_do_painel_tambem()
    {
        var cai = new Fabrica((_, _) => throw new HttpRequestException("sem rota"));
        Assert.Equal(502, (await Servico(cai).EnviarAsync("mudo")).Status);

        var recusa = new Fabrica((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var r = await Servico(recusa).EnviarAsync("mudo");
        Assert.Equal(502, r.Status);
        Assert.False(r.Enviado);

        var limite = new Fabrica((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Equal(429, (await Servico(limite).EnviarAsync("vol+")).Status);
    }

    [Fact]
    public async Task Controller_devolve_enviado_sem_prometer_e_lista_as_teclas()
    {
        var c = new TvIrController(Servico(Ok()));
        var ok = Assert.IsType<ObjectResult>(await c.Tecla(new TvIrController.TeclaRequest("vol+"), CancellationToken.None));
        Assert.Equal(200, ok.StatusCode);
        var ruim = Assert.IsType<ObjectResult>(await c.Tecla(new TvIrController.TeclaRequest("x"), CancellationToken.None));
        Assert.Equal(400, ruim.StatusCode);
        Assert.IsType<OkObjectResult>(c.Teclas());
    }
}
