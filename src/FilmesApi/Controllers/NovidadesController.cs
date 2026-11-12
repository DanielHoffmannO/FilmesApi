using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>"Novidades" do catálogo: o que chegou há pouco e ainda não foi assistido.</summary>
[ApiController]
[Route("api/catalogo")]
public class NovidadesController(FilmeService service, IConfiguration config) : ControllerBase
{
    /// <summary>O que foi adicionado recentemente (<c>DataAdicionado</c>, janela de <c>NovidadesDias</c> dias, padrão 14),
    /// sem o que já foi assistido e com os episódios da mesma série colapsados num cartão só
    /// (<c>"Série · +3 episódios novos"</c>, tocando o episódio mais novo ainda não visto). Mais novo primeiro, no máximo
    /// <c>limite</c> (padrão 20, teto 100). Cada item é <c>{filme, novos, rotulo}</c>, com <c>filme</c> no mesmo formato
    /// do catálogo. Lista vazia = não há novidades (as telas escondem a fileira).</summary>
    [HttpGet("novidades")]
    public async Task<IActionResult> ObterNovidades([FromQuery] int limite = Novidades.LimitePadrao)
    {
        var dias = config.GetValue<int?>("NovidadesDias") ?? Novidades.JanelaDiasPadrao;
        var todos = await service.ListarAsync();
        return Ok(Novidades.Montar(todos, DateTime.UtcNow, Math.Clamp(limite, 1, Novidades.LimiteMaximo), dias));
    }
}
