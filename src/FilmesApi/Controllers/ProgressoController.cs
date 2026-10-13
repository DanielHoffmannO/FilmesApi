using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>"Continuar de onde parou": ponto de retomada por filme e o botão de remover.</summary>
[ApiController]
[Route("api/filmes")]
public class ProgressoController : ControllerBase
{
    private readonly ProgressoService _progresso;

    public ProgressoController(ProgressoService progresso) => _progresso = progresso;

    /// <summary>Ponto de retomada do filme. 204 se não há nada guardado.</summary>
    [HttpGet("{id:int}/progresso")]
    public async Task<IActionResult> ObterProgresso(int id)
    {
        var p = await _progresso.ObterAsync(id);
        return p is null ? NoContent() : Ok(p);
    }

    /// <summary>Salva onde a reprodução parou. Perto do início/fim, apenas descarta a retomada.</summary>
    [HttpPut("{id:int}/progresso")]
    public async Task<IActionResult> SalvarProgresso(int id, [FromBody] ProgressoRequest req)
    {
        if (double.IsNaN(req.Posicao) || double.IsInfinity(req.Posicao) || req.Posicao < 0)
            return BadRequest(new { mensagem = "posicao inválida" });

        return await _progresso.SalvarAsync(id, req.Posicao, req.Duracao) ? NoContent() : NotFound();
    }

    /// <summary>Reprodução chegou ao fim: marca assistido e limpa a retomada.</summary>
    [HttpPost("{id:int}/concluir")]
    public async Task<IActionResult> Concluir(int id)
        => await _progresso.ConcluirAsync(id) ? NoContent() : NotFound();

    /// <summary>Tira o filme de "continuar assistindo" sem marcar como assistido — botão "x"
    /// no index.html.</summary>
    [HttpDelete("{id:int}/progresso")]
    public async Task<IActionResult> RemoverProgresso(int id)
        => await _progresso.RemoverProgressoAsync(id) ? NoContent() : NotFound();
}
