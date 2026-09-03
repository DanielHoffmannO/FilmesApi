using System.Diagnostics;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>
/// Lista as faixas de legenda embutidas (via <see cref="MediaProbeService"/>) e extrai as de
/// texto pra WebVTT, servível como <c>&lt;track&gt;</c>. O HLS/stream direto continua sem
/// legenda muxada — isto é sidecar, gerado sob demanda.
///
/// Legenda bitmap (PGS/VobSub) não dá pra converter em texto — <c>Convertivel = false</c>.
/// </summary>
public class SubtitleService
{
    private static readonly HashSet<string> CodecsTexto = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "subviewer", "subviewer1",
        "microdvd", "mpl2", "pjs", "jacosub", "sami", "realtext", "stl", "vplayer",
    };

    private readonly string _ffmpegPath;
    private readonly string _cachePath;
    private readonly MediaProbeService _probe;
    private readonly ILogger<SubtitleService> _logger;
    private readonly SemaphoreSlim _extracao = new(1, 1);

    public SubtitleService(FfmpegOptions ffmpeg, IConfiguration config, MediaProbeService probe, ILogger<SubtitleService> logger)
    {
        _ffmpegPath = ffmpeg.Ffmpeg;
        _probe = probe;
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

    /// <summary>Faixas de legenda do arquivo. <c>Idx</c> é o índice relativo (0,1,2…) usado
    /// em <c>-map 0:s:N</c> e no endpoint <c>/legenda/{idx}</c>.</summary>
    public async Task<List<LegendaInfo>> ListarAsync(string arquivoAbsoluto, CancellationToken ct)
    {
        var info = await _probe.InspecionarAsync(arquivoAbsoluto, ct);
        if (info is null) return [];

        return info.Legendas
            .Select(l => new LegendaInfo(l.IdxRelativo, l.Codec, l.Idioma, l.Titulo, l.Forced, l.Default,
                Convertivel: CodecsTexto.Contains(l.Codec)))
            .ToList();
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

            var faixa = (await ListarAsync(arquivoAbsoluto, ct)).FirstOrDefault(x => x.Idx == idxRelativo);
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
