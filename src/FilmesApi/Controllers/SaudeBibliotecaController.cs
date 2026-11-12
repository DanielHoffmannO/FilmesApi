using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>"Saúde da biblioteca": arquivos truncados/ilegíveis, duplicatas, sem português e vídeos que pedem transcode pesado — só relata, nunca
/// mexe em arquivo de mídia. A página <c>/diagnostico.html</c> desenha isso; a tarefa <c>saude-biblioteca</c> aparece em <c>/api/status</c>.</summary>
[ApiController]
[Route("api/diagnostico/biblioteca")]
public class SaudeBibliotecaController(SaudeBibliotecaService saude) : ControllerBase
{
    /// <summary>O relatório da última varredura (a diária, ou a que o botão disparou): <c>{geradoEm, total, analisados, resumo:{truncado, duplicado,
    /// sem_portugues, transcode_pesado, ilegivel}, itens:[{filmeId, titulo, arquivo, tipo, severidade, detalhe}]}</c> (+ <c>rodando</c> e <c>ultimaRodadaCompleta</c>).
    /// <c>duplicado</c> conta grupos; os itens de um grupo trazem o mesmo <c>grupo</c> e <c>candidatoManter</c> (só uma sugestão). Barato: não lê disco de mídia.</summary>
    [HttpGet]
    public IActionResult Obter() => Ok(saude.ObterRelatorio());

    /// <summary>Dispara uma varredura agora, em segundo plano (<c>202</c>). <c>409</c> se já tem uma rodando ou se o servidor está ocupado (transcode/remux,
    /// governador térmico). Protegida pelo <c>AdminToken</c> quando configurado (ver <see cref="TokenAdmin"/>).</summary>
    [HttpPost("verificar")]
    public IActionResult Verificar() => saude.Disparar() switch
    {
        DisparoDeSaude.Iniciada => Accepted(new { iniciado = true }),
        DisparoDeSaude.JaRodando => Conflict(new { iniciado = false, mensagem = "já tem uma verificação em andamento." }),
        _ => Conflict(new { iniciado = false, mensagem = "servidor ocupado (transcode/remux ou placa quente) — tente de novo mais tarde." }),
    };
}
