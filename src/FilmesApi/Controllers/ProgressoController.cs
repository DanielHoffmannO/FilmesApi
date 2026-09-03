using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>"Continuar de onde parou": ponto de retomada por filme e a lista de pendentes.</summary>
[ApiController]
[Route("api/filmes")]
public class ProgressoController : ControllerBase
{
    private readonly ProgressoService _progresso;

    public ProgressoController(ProgressoService progresso) => _progresso = progresso;

    /// <summary>Filmes com reprodução pendente ("continuar assistindo"), mais recentes primeiro.</summary>
    [HttpGet("continuar")]
    public async Task<IActionResult> Continuar()
        => Ok(await _progresso.ContinuarAssistindoAsync());

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

        // req.Duracao is > 0 já exclui NaN (NaN > 0 é false); +Infinity é que precisa de guarda.
        var duracao = req.Duracao is > 0 && !double.IsInfinity(req.Duracao.Value) ? req.Duracao : null;

        return await _progresso.SalvarAsync(id, req.Posicao, duracao) ? NoContent() : NotFound();
    }

    /// <summary>Esquece o ponto de retomada ("assistir do começo").</summary>
    [HttpDelete("{id:int}/progresso")]
    public async Task<IActionResult> LimparProgresso(int id)
        => await _progresso.LimparAsync(id) ? NoContent() : NotFound();

    /// <summary>Reprodução chegou ao fim: marca assistido e limpa a retomada.</summary>
    [HttpPost("{id:int}/concluir")]
    public async Task<IActionResult> Concluir(int id)
        => await _progresso.ConcluirAsync(id) ? NoContent() : NotFound();
}
