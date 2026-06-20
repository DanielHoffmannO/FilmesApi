using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FilmesController : ControllerBase
{
    private readonly FilmeService _service;

    public FilmesController(FilmeService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] EGenero? genero, [FromQuery] bool? assistido)
        => Ok(await _service.ListarAsync(genero, assistido));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Obter(int id)
    {
        var filme = await _service.ObterAsync(id);
        return filme is null ? NotFound() : Ok(filme);
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] FilmeRequest req)
        => Created($"/api/filmes", await _service.CriarAsync(req));

    [HttpPut("{id:int}/assistido")]
    public async Task<IActionResult> MarcarAssistido(int id)
        => await _service.MarcarAssistidoAsync(id) ? NoContent() : NotFound();

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deletar(int id)
        => await _service.DeletarAsync(id) ? NoContent() : NotFound();

    /// <summary>Escaneia pasta de mídia e importa vídeos novos.</summary>
    [HttpPost("scan")]
    public async Task<IActionResult> ScanMedia()
    {
        var novos = await _service.ScanMediaAsync();
        return Ok(new { importados = novos });
    }

    /// <summary>Stream de vídeo para assistir na TV.</summary>
    [HttpGet("{id:int}/stream")]
    public async Task<IActionResult> Stream(int id)
    {
        var filme = await _service.ObterAsync(id);
        if (filme?.ArquivoPath is null) return NotFound();

        var path = _service.ObterCaminhoAbsoluto(filme.ArquivoPath);
        if (path is null) return NotFound("Arquivo não encontrado no disco.");

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            ".webm" => "video/webm",
            ".flv" => "video/x-flv",
            _ => "application/octet-stream"
        };

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);
        return File(stream, contentType, enableRangeProcessing: true);
    }
}
