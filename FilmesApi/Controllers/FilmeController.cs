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

        /// <summary>
        /// Obtém uma lista de filmes.
        /// </summary>
        [HttpGet]
        public IActionResult ObterFilmePorId()
        {
            return Ok(_filmeService.ObterLista());
        }

        /// <summary>
        /// Adiciona um novo filme.
        /// </summary>
        /// <param name="filme">Informações do filme a ser adicionado.</param>
        [HttpPost]
        public IActionResult AdicionarFilme(FilmeDto filme)
        {
            return Ok(_filmeService.AdicionarFilme(filme));
        }

        /// <summary>
        /// Atualiza os detalhes de um filme existente.
        /// </summary>
        /// <param name="id">ID do filme a ser atualizado.</param>
        /// <param name="filme">Novas informações do filme.</param>
        [HttpPut("{id}")]
        public IActionResult AtualizarFilme(short id, FilmeDto filme)
        {
            return Ok(_filmeService.AtualizarFilme(id, filme));
        }

        /// <summary>
        /// Marca um filme como assistido.
        /// </summary>
        /// <param name="id">ID do filme a ser marcado como assistido.</param>
        [HttpPut("{id}/MarcarAssistido")]
        public IActionResult MarcarComoAssistido(short id)
        {
            return Ok(_filmeService.MarcarFilmeComoAssistido(id));
        }

        /// <summary>
        /// Remove um filme.
        /// </summary>
        /// <param name="id">ID do filme a ser removido.</param>
        [HttpDelete("{id}")]
        public IActionResult RemoverFilme(short id)
        {
            _filmeService.DeletarFilme(id);
            return Ok();
        }
    }
}
