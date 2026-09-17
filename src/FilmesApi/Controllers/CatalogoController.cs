using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Catálogo de filmes e séries: listar e sincronizar com a pasta de mídia.</summary>
[ApiController]
[Route("api/filmes")]
public class CatalogoController : ControllerBase
{
    private readonly FilmeService _service;
    private readonly ProgressoService _progresso;

    public CatalogoController(FilmeService service, ProgressoService progresso)
    {
        _service = service;
        _progresso = progresso;
    }

    /// <summary>Lista o catálogo. <c>assistido</c> filtra por já-assistido / não-assistido.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] bool? assistido)
        => Ok(await _service.ListarAsync(assistido));

    /// <summary>Catálogo já filtrado (tipo: all/filme/serie; visto: all/assistido/nao-assistido;
    /// busca: texto livre) e agrupado (filmes soltos / pasta filme+extras / série com
    /// episódios) — as 3 telas (index.html, controle.html, tv.html) só desenham isso, sem
    /// nenhuma lógica de agrupamento própria.</summary>
    [HttpGet("tela")]
    public async Task<IActionResult> Tela(
        [FromQuery] string tipo = "all", [FromQuery] string visto = "all", [FromQuery] string? busca = null)
    {
        var todos = await _service.ListarAsync();

        var continuarCandidatos = new List<Models.FilmeResponse>();
        if (tipo == "all" && visto == "all")
        {
            var continuarBase = await _progresso.ContinuarAssistindoAsync();
            var porId = todos.ToDictionary(f => f.Id);
            continuarCandidatos = continuarBase
                .Select(c => porId.GetValueOrDefault(c.Id))
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();
        }

        return Ok(FilmeService.MontarTela(todos, continuarCandidatos, tipo, visto, busca));
    }

    /// <summary>Um filme (ou episódio) pelo id.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Obter(int id)
    {
        var filme = await _service.ObterAsync(id);
        return filme is null ? NotFound() : Ok(filme);
    }

    /// <summary>Alterna o filme entre assistido e não-assistido.</summary>
    [HttpPut("{id:int}/assistido")]
    public async Task<IActionResult> MarcarAssistido(int id)
        => await _service.MarcarAssistidoAsync(id) ? NoContent() : NotFound();

    /// <summary>Sincroniza o catálogo com a pasta de mídia (importa novos, remove órfãos).</summary>
    [HttpPost("scan")]
    public async Task<IActionResult> ScanMedia()
        => Ok(await _service.ScanMediaAsync());

    /// <summary>Próximo episódio da mesma série (ordem temporada/episódio). 404 se o filme
    /// não existe; 204 quando existe mas não há próximo: não é episódio, é o último, ou é um "extra".</summary>
    [HttpGet("{id:int}/proximo")]
    public async Task<IActionResult> ProximoEpisodio(int id)
    {
        if (!await _service.ExisteAsync(id)) return NotFound();
        var prox = await _service.ProximoEpisodioAsync(id);
        return prox is null ? NoContent() : Ok(prox);
    }
}
