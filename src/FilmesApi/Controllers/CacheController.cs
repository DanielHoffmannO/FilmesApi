using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

public record IdsRequest(int[]? Ids);

/// <summary>"Manter no cache" (ver <see cref="CacheFixadoService"/>): marcar/desmarcar títulos, ver o que está pronto e limpar o resto.</summary>
[ApiController]
[Route("api/cache")]
public class CacheController(CacheFixadoService fixados, HlsTranscodeService transcode, FilmeService filmes) : ControllerBase
{
    public const int MaxIds = 500;

    /// <summary>O retrato: teto e uso, e cada título fixado com o estado da cópia (<c>pronto</c>/<c>preparando</c>) e um nome legível.</summary>
    [HttpGet]
    public async Task<IActionResult> Obter()
    {
        // o catálogo só é lido pra dar nome aos fixados: sem fixados (o caso comum), nem lê
        var ordem = fixados.Ordem;
        var catalogo = ordem.Count == 0 ? [] : (await filmes.ListarAsync()).ToDictionary(f => f.Id);
        string Nome(int id) => !catalogo.TryGetValue(id, out var f) ? $"#{id}"
            : f.EhEpisodio ? $"{f.Serie} · {(string.IsNullOrEmpty(f.Rotulo) ? f.Titulo : f.Rotulo)}" : f.Titulo;
        var itens = ordem.Select(id =>
        {
            var bytes = transcode.BytesDoRemux(id);
            return new { id, nome = Nome(id), pronto = bytes is not null, preparando = bytes is null && transcode.RemuxEmAndamento(id), bytes = bytes ?? 0 };
        }).ToList();
        return Ok(new
        {
            tetoFixadoBytes = TetoParaJson(fixados.TetoFixadoBytes),
            fixadoProntoBytes = itens.Sum(i => i.bytes),
            remuxBytes = transcode.BytesTotaisDoRemux(),
            remuxTetoBytes = TetoParaJson(transcode.TetoRemuxBytes),
            itens,
        });
    }

    [HttpPost("fixar")]
    public async Task<IActionResult> Fixar([FromBody] IdsRequest req)
    {
        if (IdsInvalidos(req)) return IdsInvalidosResposta();
        var novos = await fixados.FixarAsync(req.Ids);
        return Ok(new { novos, total = fixados.Fixados.Count });
    }

    [HttpPost("desafixar")]
    public async Task<IActionResult> Desafixar([FromBody] IdsRequest req)
    {
        if (IdsInvalidos(req)) return IdsInvalidosResposta();
        var removidos = await fixados.DesafixarAsync(req.Ids);
        return Ok(new { removidos, total = fixados.Fixados.Count });
    }

    // "sem teto" (long.MaxValue) vai pro JSON como 0
    private static long TetoParaJson(long teto) => teto == long.MaxValue ? 0 : teto;

    private static bool IdsInvalidos(IdsRequest req) => req.Ids is not { Length: > 0 and <= MaxIds };
    private BadRequestObjectResult IdsInvalidosResposta() => BadRequest(new { mensagem = $"mande de 1 a {MaxIds} ids" });

    /// <summary>Apaga o cache de reprodução que NÃO está fixado (e não está em uso). Os fixados ficam.</summary>
    [HttpPost("limpar")]
    public IActionResult Limpar() => Ok(transcode.LimparNaoFixados(fixados.Fixados));
}
