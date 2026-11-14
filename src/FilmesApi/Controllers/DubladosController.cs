using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>"Dublados": a fileira do que tem áudio em português (dublagem de verdade ou por IA), pra achar rápido o que dá pra ver sem legenda.</summary>
[ApiController]
[Route("api/catalogo")]
public class DubladosController(FilmeService service) : ControllerBase
{
    /// <summary>Os itens dublados do catálogo (<c>dublado</c> ou <c>dubladoIa</c>), com os episódios da mesma
    /// série colapsados num cartão só que toca o próximo episódio dublado não visto (<c>"Série · 12 episódios dublados"</c>). Quem tem algo por ver
    /// primeiro, os mais recentes antes; no máximo <c>limite</c> (padrão 24, teto 100). Cada item é <c>{filme, novos, rotulo}</c> — o mesmo formato
    /// de <c>/api/catalogo/novidades</c>, com <c>novos</c> = quantos episódios dublados ainda não foram vistos. Lista vazia = nada dublado (as telas
    /// escondem a fileira).</summary>
    [HttpGet("dublados")]
    public async Task<IActionResult> ObterDublados([FromQuery] int limite = Dublados.LimitePadrao)
        => Ok(Dublados.Montar(await service.ListarAsync(), Math.Clamp(limite, 1, Dublados.LimiteMaximo)));
}
