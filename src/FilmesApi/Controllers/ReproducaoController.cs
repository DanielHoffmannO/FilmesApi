using System.Text.RegularExpressions;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Entrega do vídeo: decisão stream-direto vs HLS, playlist/segments, legendas e keepalive.</summary>
[ApiController]
[Route("api/filmes")]
public partial class ReproducaoController : ControllerBase
{
    private readonly FilmeService _service;
    private readonly HlsTranscodeService _transcode;
    private readonly SubtitleService _legendas;

    public ReproducaoController(FilmeService service, HlsTranscodeService transcode, SubtitleService legendas)
    {
        _service = service;
        _transcode = transcode;
        _legendas = legendas;
    }

    /// <summary>Keepalive do player: "ainda tem alguém assistindo este filme". Sem isso, o
    /// transcode em andamento é abortado depois de <c>HlsOrphanTimeoutSeconds</c> sem sinal.</summary>
    [HttpPost("{id:int}/assistindo")]
    public IActionResult Assistindo(int id)
    {
        _transcode.RegistrarInteresse(id);
        return NoContent();
    }

    /// <summary>"Dá pra tocar o arquivo direto?" — sem disparar transcode. A feia.html
    /// (TV antiga, que não roda HLS) usa isso pra escolher entre /stream e /original.</summary>
    [HttpGet("{id:int}/pode-direto")]
    public async Task<IActionResult> PodeDireto(int id, CancellationToken ct)
    {
        var (path, erro) = await ResolverCaminhoAsync(id);
        if (erro is not null) return erro;
        return Ok(new { compativel = await _transcode.PodeStreamDiretoAsync(path!, ct) });
    }

    /// <summary>Estado do vídeo: <c>compativel</c> / <c>preparando</c> / <c>disponivel</c> /
    /// <c>erro</c>. Sempre 200 quando o arquivo existe — "erro" é o transcode que falhou,
    /// não a consulta.</summary>
    [HttpGet("{id:int}/stream-status")]
    public async Task<IActionResult> ObterStreamStatus(int id, CancellationToken ct)
    {
        var (status, _, erro) = await ResolverStatusAsync(id, ct);
        if (erro is not null) return erro;

        return Ok(new { status = status.ToString().ToLowerInvariant() });
    }

    /// <summary>Stream direto do vídeo quando o navegador já toca o codec/container original.</summary>
    [HttpGet("{id:int}/stream")]
    public async Task<IActionResult> Stream(int id, CancellationToken ct)
    {
        var (path, erro) = await ResolverCaminhoAsync(id);
        if (erro is not null) return erro;

        // Usa o check "seco" (sem ObterStatusAsync) de propósito: pedir /stream de um arquivo
        // incompatível não deve disparar um transcode que ninguém vai consumir.
        if (!await _transcode.PodeStreamDiretoAsync(path!, ct))
            return Conflict(new { mensagem = "Este vídeo precisa de HLS, use /hls/playlist.m3u8." });

        return ServirComRange(path!);
    }

    /// <summary>Manifest HLS: dispara/reusa o job de transcodificação e serve a playlist assim que ela existir.</summary>
    [HttpGet("{id:int}/hls/playlist.m3u8")]
    public async Task<IActionResult> HlsPlaylist(int id, CancellationToken ct)
    {
        var (status, caminho, erro) = await ResolverStatusAsync(id, ct);
        if (erro is not null) return erro;

        if (status is StreamStatus.Erro)
            return StatusCode(StatusCodes.Status500InternalServerError, new { mensagem = "Falha ao converter o vídeo." });
        if (status is StreamStatus.Compativel)
            return Conflict(new { mensagem = "Este vídeo é compatível direto, use /stream." });
        if (status is StreamStatus.Preparando)
            return StatusCode(StatusCodes.Status202Accepted);

        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(caminho!, "application/vnd.apple.mpegurl");
    }

    /// <summary>Segments HLS (.ts) gerados para o filme.</summary>
    [HttpGet("{id:int}/hls/{arquivo}")]
    public IActionResult HlsSegmento(int id, string arquivo)
    {
        if (!SegmentoValido().IsMatch(arquivo)) return NotFound();

        _transcode.RegistrarInteresse(id);
        var caminho = Path.Combine(_transcode.DiretorioCache(id), arquivo);
        if (!System.IO.File.Exists(caminho)) return NotFound();

        // Não marcar como "immutable": se um encode via rkmpp falhar no meio, o job apaga e
        // regrava os mesmos nomes de segment via libx264 — um cliente que já buscou a versão
        // antiga não pode ficar preso a ela por até 1 ano.
        Response.Headers.CacheControl = "public, max-age=3600";
        return PhysicalFile(caminho, "video/mp2t");
    }

    /// <summary>Serve sempre o arquivo original, sem nenhuma transcodificação — para players externos (VLC etc.).</summary>
    [HttpGet("{id:int}/original")]
    public async Task<IActionResult> Original(int id)
    {
        var (path, erro) = await ResolverCaminhoAsync(id);
        return erro ?? ServirComRange(path!);
    }

    /// <summary>Faixas de legenda embutidas no arquivo (as de texto viram .vtt via o endpoint abaixo).</summary>
    [HttpGet("{id:int}/legendas")]
    public async Task<IActionResult> Legendas(int id, CancellationToken ct)
    {
        var (path, erro) = await ResolverCaminhoAsync(id);
        if (erro is not null) return erro;
        return Ok(await _legendas.ListarAsync(path!, ct));
    }

    /// <summary>Uma faixa de legenda de texto convertida pra WebVTT. 404 se o índice não
    /// existe ou a faixa é bitmap (PGS/VobSub — não dá pra converter em texto).</summary>
    [HttpGet("{id:int}/legenda/{idx:int}")]
    public async Task<IActionResult> Legenda(int id, int idx, CancellationToken ct)
    {
        var (path, erro) = await ResolverCaminhoAsync(id);
        if (erro is not null) return erro;

        var vtt = await _legendas.ObterVttAsync(id, path!, idx, ct);
        if (vtt is null) return NotFound();

        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(vtt, "text/vtt; charset=utf-8");
    }

    private async Task<(string? Path, IActionResult? Erro)> ResolverCaminhoAsync(int id)
    {
        var arquivoPath = await _service.ObterArquivoPathAsync(id);
        if (arquivoPath is null) return (null, NotFound());

        var path = _service.ObterCaminhoAbsoluto(arquivoPath);
        return path is null ? (null, NotFound("Arquivo não encontrado no disco.")) : (path, null);
    }

    // `Erro` só cobre a falha de resolver o arquivo no disco (404). StreamStatus.Erro é um
    // estado de resposta válido — cada endpoint decide o código (stream-status devolve 200).
    private async Task<(StreamStatus Status, string? Caminho, IActionResult? Erro)> ResolverStatusAsync(int id, CancellationToken ct)
    {
        var (path, erro) = await ResolverCaminhoAsync(id);
        if (erro is not null) return (default, null, erro);

        var (status, caminho) = await _transcode.ObterStatusAsync(id, path!, ct);
        return (status, caminho, null);
    }

    private IActionResult ServirComRange(string caminho)
    {
        var contentType = Path.GetExtension(caminho).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };

        var stream = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);
        return File(stream, contentType, enableRangeProcessing: true);
    }

    [GeneratedRegex(@"^seg_\d{5}\.ts$")]
    private static partial Regex SegmentoValido();
}
