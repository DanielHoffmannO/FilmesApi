using Filmes.Application.Interfece;
using Filmes.Domain.Dto;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class FilmeController : ControllerBase
    {
        private readonly IFilmeService _filmeService;

        public FilmeController(IFilmeService filmeService)
        {
            _filmeService = filmeService;
        }

        [HttpGet]
        public IActionResult ObterFilmePorId()
        {
            return Ok(_filmeService.ObterLista());
        }

        [HttpPost]
        public IActionResult AdicionarFilme(FilmeDto filme)
        {
            return Ok(_filmeService.AdicionarFilme(filme));
        }

        [HttpPut("{id}")]
        public IActionResult AtualizarFilme(short id, FilmeDto filme)
        {
            return Ok(_filmeService.AtualizarFilme(id, filme));
        }

        [HttpPut("{id}/MarcarAssistido")]
        public IActionResult MarcarComoAssistido(short id)
        {
            return Ok(_filmeService.MarcarFilmeComoAssistido(id));
        }

        [HttpDelete("{id}")]
        public IActionResult RemoverFilme(short id)
        {
            _filmeService.DeletarFilme(id);
            return Ok();
        }
    }
}
