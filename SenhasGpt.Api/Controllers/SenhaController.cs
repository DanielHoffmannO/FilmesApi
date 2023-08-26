using Microsoft.AspNetCore.Mvc;
using SenhasGpt.Application.Interfece;

namespace SenhaApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SenhaController : ControllerBase
    {
        private readonly ISenhaService _senhaservice;

        public SenhaController(ISenhaService senhaService)
        {
            _senhaservice = senhaService;
        }

        /// <summary>
        /// Obtém uma lista de Senha.
        /// </summary>
        [HttpGet]
        public IActionResult GetSenhas()
        {
            return Ok(_senhaservice.ObterLista());
        }

        [HttpPost]
        public IActionResult NovaSenha(string teste)
        {
            return Ok(_senhaservice.CriarNovaSenha());
        }
    }
}
