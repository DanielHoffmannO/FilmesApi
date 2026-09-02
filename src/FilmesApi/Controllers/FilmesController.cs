using System.Text.RegularExpressions;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class FilmesController : ControllerBase
{
    private readonly FilmeService _service;
    private readonly HlsTranscodeService _transcode;
    private readonly ProgressoService _progresso;
    private readonly SubtitleService _legendas;

    public FilmesController(FilmeService service, HlsTranscodeService transcode, ProgressoService progresso, SubtitleService legendas)
    {
        _service = service;
        _transcode = transcode;
        _progresso = progresso;
        _legendas = legendas;
    }

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] bool? assistido)
        => Ok(await _service.ListarAsync(assistido));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Obter(int id)
    {
        var filme = await _service.ObterAsync(id);
        return filme is null ? NotFound() : Ok(filme);
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] FilmeRequest req)
        => Created($"/api/filmes", await _service.CriarAsync(req));

    [HttpPut("{id:int}/assistido")]
    public async Task<IActionResult> MarcarAssistido(int id)
        => await _service.MarcarAssistidoAsync(id) ? NoContent() : NotFound();

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deletar(int id)
        => await _service.DeletarAsync(id) ? NoContent() : NotFound();

    /// <summary>Sincroniza o catálogo com a pasta de mídia (importa novos, remove órfãos).</summary>
    [HttpPost("scan")]
    public async Task<IActionResult> ScanMedia()
    {
        var r = await _service.ScanMediaAsync();
        return Ok(new { importados = r.Importados, removidos = r.Removidos });
    }

    /// <summary>Filmes com reprodução pendente ("continuar assistindo"), mais recentes primeiro.</summary>
    [HttpGet("continuar")]
    public async Task<IActionResult> Continuar()
        => Ok(await _progresso.ContinuarAssistindoAsync());

    /// <summary>Ponto de retomada do filme. 204 se não há nada guardado.</summary>
    [HttpGet("{id:int}/progresso")]
    public async Task<IActionResult> ObterProgresso(int id)
    {
        var p = await _progresso.ObterAsync(id);
        return p is null ? NoContent() : Ok(p);
    }

    /// <summary>Salva onde a reprodução parou. Perto do início/fim, apenas descarta a retomada.</summary>
    [HttpPut("{id:int}/progresso")]
    public async Task<IActionResult> SalvarProgresso(int id, [FromBody] ProgressoRequest req)
    {
        if (double.IsNaN(req.Posicao) || double.IsInfinity(req.Posicao) || req.Posicao < 0)
            return BadRequest(new { mensagem = "posicao inválida" });

        // req.Duracao is > 0 já exclui NaN (NaN > 0 é false); +Infinity é que precisa de guarda.
        var duracao = req.Duracao is > 0 && !double.IsInfinity(req.Duracao.Value) ? req.Duracao : null;

        return await _progresso.SalvarAsync(id, req.Posicao, duracao) ? NoContent() : NotFound();
    }

    /// <summary>Esquece o ponto de retomada ("assistir do começo").</summary>
    [HttpDelete("{id:int}/progresso")]
    public async Task<IActionResult> LimparProgresso(int id)
        => await _progresso.LimparAsync(id) ? NoContent() : NotFound();

    /// <summary>Reprodução chegou ao fim: marca assistido e limpa a retomada.</summary>
    [HttpPost("{id:int}/concluir")]
    public async Task<IActionResult> Concluir(int id)
        => await _progresso.ConcluirAsync(id) ? NoContent() : NotFound();

    /// <summary>Próximo episódio da mesma série (ordem S/E). 204 quando não há próximo:
    /// este não é episódio, é o último, ou é um "extra" (deleted scene/trailer) que não
    /// entra na sequência. Série/episódio vem do nome do arquivo (<see cref="MediaNomeParser"/>).</summary>
    [HttpGet("{id:int}/proximo")]
    public async Task<IActionResult> ProximoEpisodio(int id)
    {
        var atual = await _service.ObterAsync(id);
        if (atual is null) return NotFound();
        if (!MediaNomeParser.EhEpisodio(atual.ArquivoPath)) return NoContent();

        var chave = MediaNomeParser.ChaveSerie(atual.ArquivoPath);
        var todos = await _service.ListarAsync();

        var episodios = todos
            .Where(f => MediaNomeParser.EhEpisodio(f.ArquivoPath)
                        && !MediaNomeParser.EhExtra(f.ArquivoPath)
                        && MediaNomeParser.ChaveSerie(f.ArquivoPath) == chave)
            .Select(f => new { Filme = f, Ordem = MediaNomeParser.OrdemEpisodio(f.Titulo) })
            .OrderBy(x => x.Ordem?.Temporada ?? 0)
            .ThenBy(x => x.Ordem?.Episodio ?? 0)
            .ThenBy(x => x.Filme.Titulo, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Filme)
            .ToList();

        var i = episodios.FindIndex(f => f.Id == id);
        if (i < 0 || i + 1 >= episodios.Count) return NoContent();
        return Ok(episodios[i + 1]);
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

    /// <summary>Verifica se o vídeo já pode ser tocado direto, via HLS, ou se ainda está preparando.</summary>
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
        var (status, caminho, erro) = await ResolverStatusAsync(id, ct);
        if (erro is not null) return erro;

        if (status is not StreamStatus.Compativel || caminho is null)
            return Conflict(new { mensagem = "Este vídeo precisa de HLS, use /hls/playlist.m3u8." });

        return ServirComRange(caminho);
    }

    /// <summary>Manifest HLS: dispara/reusa o job de transcodificação e serve a playlist assim que ela existir.</summary>
    [HttpGet("{id:int}/hls/playlist.m3u8")]
    public async Task<IActionResult> HlsPlaylist(int id, CancellationToken ct)
    {
        var (status, caminho, erro) = await ResolverStatusAsync(id, ct);
        if (erro is not null) return erro;

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

    private async Task<(StreamStatus Status, string? Caminho, IActionResult? Erro)> ResolverStatusAsync(int id, CancellationToken ct)
    {
        var (path, erro) = await ResolverCaminhoAsync(id);
        if (erro is not null) return (default, null, erro);

        var (status, caminho) = await _transcode.ObterStatusAsync(id, path!, ct);
        if (status is StreamStatus.Erro)
            return (status, null, StatusCode(StatusCodes.Status500InternalServerError, "Falha ao converter o vídeo."));

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
