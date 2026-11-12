using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Webhook do Sonarr/Radarr: quando o *arr importa um arquivo, avisa aqui e o catálogo é sincronizado na hora
/// (ver <c>docs/webhook-arr.md</c>). Desligado (404) enquanto <c>Webhook:Chave</c> estiver vazia.</summary>
[ApiController]
[Route("api/webhook")]
public class WebhookArrController(IConfiguration config, ScanPeriodicoService scan, ILogger<WebhookArrController> logger) : ControllerBase
{
    /// <summary>Recebe o payload padrão de Webhook do Sonarr/Radarr. Autenticação: <c>?chave=</c> igual a <c>Webhook:Chave</c>
    /// (a chave nunca é logada). <c>Test</c> → <c>200 {ok:true}</c>; importação/renomeio/remoção → pede um scan (coalescido,
    /// em segundo plano) e responde 200 na hora; o resto é ignorado com 200. <c>404</c> com o webhook desligado, <c>401</c>
    /// com a chave errada, <c>400</c> se o corpo não for um webhook do *arr, <c>413</c> se passar de 1 MB.</summary>
    [HttpPost("arr")]
    public async Task<IActionResult> Arr([FromQuery] string? chave)
    {
        if (!WebhookArr.Habilitado(config[WebhookArr.ConfigChave])) return NotFound();
        if (!WebhookArr.ChaveConfere(config[WebhookArr.ConfigChave], chave))
        {
            logger.LogWarning("Webhook do *arr recusado: chave ausente ou errada (origem {Ip}).", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { ok = false, mensagem = "chave ausente ou errada." });
        }

        if (Request.ContentLength > WebhookArr.TamanhoMaximoDoCorpo) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        var corpo = await LerCorpoAsync(HttpContext.RequestAborted);
        if (corpo is null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var r = WebhookArr.Interpretar(corpo);
        switch (r.Acao)
        {
            case AcaoDoWebhook.Invalido:
                return BadRequest(new { ok = false, mensagem = "o corpo não é um webhook do Sonarr/Radarr (JSON com eventType)." });
            case AcaoDoWebhook.Teste:
                logger.LogInformation("Webhook do {Origem}: teste recebido.", r.Origem);
                return Ok(new { ok = true });
            case AcaoDoWebhook.Escanear:
                logger.LogInformation("Webhook do {Origem}: {Evento} — scan agendado.", r.Origem, r.Evento);
                scan.Solicitar();
                return Ok(new { ok = true, scan = "agendado" });
            default:
                logger.LogDebug("Webhook do {Origem}: {Evento} ignorado.", r.Origem, r.Evento);
                return Ok(new { ok = true, ignorado = true });
        }
    }

    // Lê o corpo como texto, com teto (chunked não traz Content-Length). null = passou do teto.
    private async Task<string?> LerCorpoAsync(CancellationToken ct)
    {
        using var leitor = new StreamReader(Request.Body, System.Text.Encoding.UTF8);
        var buffer = new char[8192];
        var texto = new System.Text.StringBuilder();
        int n;
        while ((n = await leitor.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            texto.Append(buffer, 0, n);
            if (texto.Length > WebhookArr.TamanhoMaximoDoCorpo) return null;
        }
        return texto.ToString();
    }
}
