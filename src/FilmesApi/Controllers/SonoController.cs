using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FilmesApi.Controllers;

/// <summary>Timer de sono e "Ainda está assistindo?" (mesma rota <c>api/player</c> do <see cref="PlayerController"/>). O estado volta no <c>/api/player/state</c>
/// (<c>sono</c>, <c>presenca</c>, <c>noFim</c>) e a TV (tv.html) o aplica; as regras são puras e estão em <c>SonoEPresenca.cs</c>.</summary>
[ApiController]
[Route("api/player")]
[TypeFilter(typeof(InteracaoHumanaFilter))]
public class SonoController(PlayerStateService state) : ControllerBase
{
    /// <summary>O controle arma ou cancela o sono: <c>{ minutos, aoFimDoEpisodio }</c>. <c>minutos</c> &gt; 0 = parar em N minutos (máx. 12 h); <c>aoFimDoEpisodio</c> = a TV não
    /// avança pro próximo; <c>minutos</c> = 0 sem <c>aoFimDoEpisodio</c> cancela. Um pedido novo substitui o anterior. Vencido, a TV para e mostra "Boa noite".</summary>
    [HttpPost("sono")]
    public IActionResult Sono([FromBody] SonoRequest req)
    {
        state.DefinirSono(req.Minutos, req.AoFimDoEpisodio);
        return Ok(state.Snapshot());
    }

    /// <summary>A TV avisa que o episódio acabou com o sono "ao fim" armado: o servidor dispara (para o player, "Boa noite").</summary>
    [HttpPost("sono-fim")]
    public IActionResult SonoFim()
    {
        state.DispararSonoAoFim();
        return NoContent();
    }

    /// <summary>A TV avisa que, no fim do episódio, não iniciou o próximo e está perguntando "Ainda está assistindo?" (já avançou sozinha vezes demais).</summary>
    [HttpPost("presenca-pergunta")]
    public IActionResult PresencaPergunta([FromBody] PresencaPerguntaRequest req)
    {
        state.PerguntarSeAindaAssiste(req.Rotulo);
        return NoContent();
    }

    /// <summary>O botão "Continuar" do aviso no controle: confirma a presença e manda a TV iniciar o próximo (como o OK local).</summary>
    [HttpPost("presenca-continuar")]
    public IActionResult Continuar()
    {
        state.ConfirmarPresenca();
        return Ok(state.Snapshot());
    }

    /// <summary>A TV avisa que uma tecla foi apertada nela (no máximo uma vez a cada poucos segundos): é interação humana. O filtro já zerou a conta.</summary>
    [HttpPost("interacao")]
    public IActionResult Interacao() => NoContent();
}

/// <summary>Antes de cada ação do player: se ela é um comando de uma pessoa (<see cref="InteracaoHumana"/>), zera a conta dos avanços automáticos; se é o avanço
/// automático da TV (<c>/selecionar/{id}?auto=1</c>), conta mais um. Um filtro só, em vez de uma linha em cada ação.</summary>
public class InteracaoHumanaFilter(PlayerStateService estado) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext contexto)
    {
        var acao = (contexto.ActionDescriptor as ControllerActionDescriptor)?.ActionName ?? "";
        var avancoAutomatico = contexto.HttpContext.Request.Query["auto"] == "1";
        switch (InteracaoHumana.Efeito(acao, avancoAutomatico, estado.FilmeId is not null))
        {
            case EfeitoNaPresenca.Zerar: estado.RegistrarInteracao(); break;
            case EfeitoNaPresenca.ContarAvanco: estado.RegistrarAvancoAutomatico(); break;
        }
    }

    public void OnActionExecuted(ActionExecutedContext contexto) { }
}
