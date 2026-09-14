using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Catálogo de filmes e séries: listar e sincronizar com a pasta de mídia.</summary>
[ApiController]
[Route("api/filmes")]
public class CatalogoController : ControllerBase
{
    private readonly FilmeService _service;

    public CatalogoController(FilmeService service) => _service = service;

    /// <summary>Lista o catálogo. <c>assistido</c> filtra por já-assistido / não-assistido.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] bool? assistido)
        => Ok(await _service.ListarAsync(assistido));

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
