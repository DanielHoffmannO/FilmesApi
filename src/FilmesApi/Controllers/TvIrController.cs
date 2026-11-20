using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Teclas do controle IR da TV (volume, canal, setas…) pelo painel. "Enviado" não promete que a TV reagiu: o IR não devolve estado.</summary>
[ApiController]
[Route("api/tv")]
public class TvIrController(TvIrService ir) : ControllerBase
{
    public record TeclaRequest(string? Tecla);

    /// <summary>Teclas aceitas (lista fechada) e se o IR está configurado — a tela só mostra os botões quando <c>configurado</c>.</summary>
    [HttpGet("teclas")]
    public IActionResult Teclas() => Ok(new { configurado = ir.Configurado, teclas = TvIrService.Teclas });

    /// <summary><c>{ tecla }</c> → o servidor manda ao painel. 200 = o painel aceitou ("enviado"); erro volta como <c>{ enviado:false, mensagem }</c> em português.</summary>
    [HttpPost("tecla")]
    public async Task<IActionResult> Tecla([FromBody] TeclaRequest req, CancellationToken ct)
    {
        var r = await ir.EnviarAsync(req.Tecla, ct);
        return StatusCode(r.Status, new { enviado = r.Enviado, mensagem = r.Mensagem });
    }
}
