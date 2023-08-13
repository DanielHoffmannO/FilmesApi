using Filmes.Application.Interfece;
using Filmes.Application.ViewModel;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

[ApiController]
[Route("[controller]")]
public class FilmeController : ControllerBase
{
    private readonly IFilmeService _filmeService;
    public FilmeController(IFilmeService filmeService)
    {
        _filmeService = filmeService;
    }

    [HttpPost]
    public IActionResult AdicionarFilme(AdicionarFilme filme)
    {
        return Ok(_filmeService.GetById());
    }
}
        