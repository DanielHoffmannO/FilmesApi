using System.Diagnostics;
using System.Text.Json;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>
/// Lista as faixas de legenda embutidas de um arquivo e extrai as de texto pra WebVTT
/// (servível como <c>&lt;track&gt;</c>). O HLS/stream direto continua sem legenda muxada —
/// isto é sidecar, gerado sob demanda e cacheado ao lado dos segments do filme.
///
/// Legenda bitmap (PGS/VobSub) não dá pra converter em texto — fica listada como
/// <c>Convertivel = false</c> e o endpoint .vtt recusa.
/// </summary>
public class SubtitleService
{
    private static readonly HashSet<string> CodecsTexto = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "subviewer", "subviewer1",
        "microdvd", "mpl2", "pjs", "jacosub", "sami", "realtext", "stl", "vplayer",
    };

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly string _cachePath;
    private readonly ILogger<SubtitleService> _logger;
    private readonly SemaphoreSlim _extracao = new(1, 1);

    public SubtitleService(IConfiguration config, ILogger<SubtitleService> logger)
    {
        _ffmpegPath = config.GetValue<string>("FfmpegPath") ?? "ffmpeg";
        _ffprobePath = config.GetValue<string>("FfprobePath") ?? "ffprobe";
        // Cache próprio, longe do churn do HLS (LimparDir a cada fallback de encode, poda por
        // teto, limpeza pós-restart) — senão o .vtt some e é re-extraído toda hora.
        _cachePath = config.GetValue<string>("SubtitleCachePath") ?? "/data/subs";
        _logger = logger;
        try { Directory.CreateDirectory(_cachePath); } catch (IOException) { }
    }

    /// <summary>Apaga o .vtt cacheado do filme (chamado ao deletar o filme do catálogo).</summary>
    public void LimparCache(int filmeId)
    {
        try
        {
            var dir = Path.Combine(_cachePath, filmeId.ToString());
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Faixas de legenda do arquivo, na ordem em que o ffprobe as devolve
    /// (essa ordem = o índice relativo usado em <c>-map 0:s:N</c> e no endpoint .vtt).</summary>
    public async Task<List<LegendaInfo>> ListarAsync(string arquivoAbsoluto, CancellationToken ct)
    {
        // stderr NÃO redirecionado de propósito: com "-v error" ele é mínimo, e um pipe de
        // stderr que a gente não drena trava o ffprobe quando enche (deadlock).
        var psi = new ProcessStartInfo(_ffprobePath) { RedirectStandardOutput = true };
        foreach (var arg in new[]
        {
            "-v", "error", "-select_streams", "s",
            "-show_entries", "stream=index,codec_name:stream_tags=language,title:disposition=forced,default",
            "-of", "json", arquivoAbsoluto,
        })
            psi.ArgumentList.Add(arg);

        string saida;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));  // arquivo corrompido não pode pendurar isto pra sempre
            using var proc = Process.Start(psi);
            if (proc is null) return [];
            try
            {
                saida = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* já morreu */ }
                _logger.LogWarning("ffprobe de legendas estourou 30s para {Arquivo} — abortado.", arquivoAbsoluto);
                return [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe de legendas falhou para {Arquivo}.", arquivoAbsoluto);
            return [];
        }

        var lista = new List<LegendaInfo>();
        try
        {
            using var doc = JsonDocument.Parse(saida);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)) return lista;

            var idxRel = 0;
            foreach (var s in streams.EnumerateArray())
            {
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                string? idioma = s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var l)
                    ? l.GetString() : null;
                string? titulo = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("title", out var t)
                    ? t.GetString() : null;
                var forced = s.TryGetProperty("disposition", out var disp)
                    && disp.TryGetProperty("forced", out var f) && f.GetInt32() == 1;
                var padrao = disp.ValueKind == JsonValueKind.Object
                    && disp.TryGetProperty("default", out var d) && d.GetInt32() == 1;

                lista.Add(new LegendaInfo(idxRel, codec, idioma, titulo, forced, padrao,
                    Convertivel: CodecsTexto.Contains(codec)));
                idxRel++;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Não deu pra parsear as legendas de {Arquivo}.", arquivoAbsoluto);
        }
        return lista;
    }

    /// <summary>Caminho do .vtt (extraindo/cacheando se preciso), ou null se o índice não
    /// existe, não é texto, ou a extração falhou.</summary>
    public async Task<string?> ObterVttAsync(int filmeId, string arquivoAbsoluto, int idxRelativo, CancellationToken ct)
    {
        if (idxRelativo < 0) return null;

        var dir = Path.Combine(_cachePath, filmeId.ToString());
        var destino = Path.Combine(dir, $"sub_{idxRelativo}.vtt");
        if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

        await _extracao.WaitAsync(ct);
        try
        {
            if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

            var faixas = await ListarAsync(arquivoAbsoluto, ct);
            var faixa = faixas.FirstOrDefault(x => x.Idx == idxRelativo);
            if (faixa is null || !faixa.Convertivel)
            {
                _logger.LogInformation("Legenda {Idx} do filme {Id} não é texto ({Codec}) — não dá pra servir como .vtt.",
                    idxRelativo, filmeId, faixa?.Codec ?? "?");
                return null;
            }

            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo(_ffmpegPath);
            foreach (var arg in new[]
            {
                "-y", "-i", arquivoAbsoluto,
                "-map", $"0:s:{idxRelativo}", "-c:s", "webvtt", "-f", "webvtt",
                destino,
            })
                psi.ArgumentList.Add(arg);

            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(3));
            if (exit != 0 || !File.Exists(destino) || new FileInfo(destino).Length == 0)
            {
                _logger.LogWarning("Extração da legenda {Idx} do filme {Id} falhou (exit {Exit}): {Err}",
                    idxRelativo, filmeId, exit, stderr);
                try { if (File.Exists(destino)) File.Delete(destino); } catch (IOException) { }
                return null;
            }
            return destino;
        }
        finally
        {
            _extracao.Release();
        }
    }
}
