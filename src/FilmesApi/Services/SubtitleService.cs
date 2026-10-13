using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>
/// Lista as faixas de legenda do vídeo — embutidas (via <see cref="MediaProbeService"/>) e
/// externas (.srt/.ass soltos ao lado do arquivo, ver <see cref="LegendaExternaLocalizador"/>)
/// — e converte as de texto pra WebVTT, servível como <c>&lt;track&gt;</c>. O HLS/stream
/// direto continua sem legenda muxada — isto é sidecar, gerado sob demanda.
///
/// Índice (<c>Idx</c>): as embutidas ocupam 0..N-1 (ordem do ffprobe) e as externas continuam
/// de N em diante, então o endpoint <c>/legenda/{idx}</c> e o <c>legendaIdx</c> do controle
/// remoto seguem sendo um inteiro só, sem o front precisar saber a origem.
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

    // Legenda de texto tem poucas centenas de KB; acima disso é arquivo errado (ou lixo).
    private const long LimiteBytesLegendaExterna = 5_000_000;

    private readonly string _ffmpegPath;
    private readonly string _cachePath;
    private readonly MediaProbeService _probe;
    private readonly ILogger<SubtitleService> _logger;
    private readonly SemaphoreSlim _extracao = new(1, 1);
    // Conversão que falhou não é tentada de novo a cada pedido (cada tentativa ocupa o _extracao
    // e roda ffmpeg): espera alguns minutos antes de aceitar outra.
    private static readonly TimeSpan EsperaAposFalha = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTime> _falhas = new();
    private readonly CancellationToken _pararToken;

    public SubtitleService(FfmpegOptions ffmpeg, IConfiguration config, MediaProbeService probe,
        ILogger<SubtitleService> logger, IHostApplicationLifetime lifetime)
    {
        _ffmpegPath = ffmpeg.Ffmpeg;
        _probe = probe;
        _pararToken = lifetime.ApplicationStopping;
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

    /// <summary>Faixas de legenda do arquivo (embutidas, depois externas). <c>Idx</c> é o
    /// índice usado no endpoint <c>/legenda/{idx}</c>; nas embutidas é o relativo (0,1,2…)
    /// de <c>-map 0:s:N</c>.</summary>
    public async Task<List<LegendaInfo>> ListarAsync(string arquivoAbsoluto, CancellationToken ct) =>
        (await ListarCompletoAsync(arquivoAbsoluto, ct)).Todas;

    private async Task<(List<LegendaInfo> Todas, int Embutidas, IReadOnlyList<LegendaExterna> Externas)>
        ListarCompletoAsync(string arquivoAbsoluto, CancellationToken ct)
    {
        var info = await _probe.InspecionarAsync(arquivoAbsoluto, ct);
        var todas = (info?.Legendas ?? [])
            .Select(l => new LegendaInfo(l.IdxRelativo, l.Codec, l.Idioma, l.Titulo, l.Forced, l.Default,
                Convertivel: CodecsTexto.Contains(l.Codec)))
            .ToList();
        var embutidas = todas.Count;

        var externas = LegendaExternaLocalizador.Localizar(arquivoAbsoluto);
        for (var i = 0; i < externas.Count; i++)
        {
            var e = externas[i];
            todas.Add(new LegendaInfo(embutidas + i, Path.GetExtension(e.Caminho).TrimStart('.').ToLowerInvariant(),
                e.Idioma, e.Titulo, e.Forced, Default: false, Convertivel: true));
        }
        return (todas, embutidas, externas);
    }

    /// <summary>Caminho do .vtt (extraindo/cacheando se preciso), ou null se o índice não
    /// existe, não é texto, ou a extração falhou.</summary>
    public async Task<string?> ObterVttAsync(int filmeId, string arquivoAbsoluto, int idxRelativo, CancellationToken ct)
    {
        if (idxRelativo < 0) return null;

        var dir = Path.Combine(_cachePath, filmeId.ToString());
        var destino = Path.Combine(dir, $"sub_{idxRelativo}.vtt");
        if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

        var (todas, embutidas, externas) = await ListarCompletoAsync(arquivoAbsoluto, ct);
        var faixa = todas.FirstOrDefault(x => x.Idx == idxRelativo);
        if (faixa is null || !faixa.Convertivel)
        {
            _logger.LogInformation("Legenda {Idx} do filme {Id} não é texto ({Codec}) — não dá pra servir como .vtt.",
                idxRelativo, filmeId, faixa?.Codec ?? "?");
            return null;
        }

        if (idxRelativo >= embutidas)
            return await ObterVttExternoAsync(filmeId, externas[idxRelativo - embutidas], dir, ct);

        if (FalhouRecentemente(destino)) return null;
        await _extracao.WaitAsync(ct);
        string? parcial = null;
        try
        {
            if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

            Directory.CreateDirectory(dir);
            // Grava num .tmp e só move no fim: se o processo morrer no meio (shutdown), o
            // cache nunca vê um .vtt pela metade com Length > 0.
            parcial = destino + ".tmp";
            var psi = new ProcessStartInfo(_ffmpegPath);
            foreach (var arg in new[]
            {
                "-y", "-i", arquivoAbsoluto,
                "-map", $"0:s:{idxRelativo}", "-c:s", "webvtt", "-f", "webvtt",
                parcial,
            })
                psi.ArgumentList.Add(arg);

            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(3), null, _pararToken);
            if (exit != 0 || !File.Exists(parcial) || new FileInfo(parcial).Length == 0)
            {
                _logger.LogWarning("Extração da legenda {Idx} do filme {Id} falhou (exit {Exit}): {Err}",
                    idxRelativo, filmeId, exit, stderr);
                _falhas[destino] = DateTime.UtcNow;
                return null;
            }
            File.Move(parcial, destino, overwrite: true);
            return destino;
        }
        finally
        {
            ApagarSilencioso(parcial);
            _extracao.Release();
        }
    }

    /// <summary>Converte um .srt/.ass externo pra WebVTT. O texto é relido como UTF-8 antes do
    /// ffmpeg (que assume UTF-8): legenda brasileira costuma ser Windows-1252 e viraria
    /// "a��o". Cache por caminho+mtime+tamanho — trocar o arquivo invalida sozinho, sem
    /// depender do índice (que muda se aparecer outra legenda na pasta).</summary>
    private async Task<string?> ObterVttExternoAsync(int filmeId, LegendaExterna externa, string dir, CancellationToken ct)
    {
        FileInfo fi;
        try { fi = new FileInfo(externa.Caminho); if (!fi.Exists) return null; }
        catch (IOException) { return null; }
        if (fi.Length > LimiteBytesLegendaExterna) return null;

        var chave = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{externa.Caminho}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}")))[..16];
        var destino = Path.Combine(dir, $"ext_{chave}.vtt");
        if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

        if (FalhouRecentemente(destino)) return null;
        await _extracao.WaitAsync(ct);
        string? origem = null, parcial = null;
        try
        {
            if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

            Directory.CreateDirectory(dir);
            var texto = LegendaExternaLocalizador.DecodificarTexto(await File.ReadAllBytesAsync(externa.Caminho, ct));
            // Nomes distintos do destino: uma legenda externa que já é .vtt não pode ser
            // entrada e saída do ffmpeg ao mesmo tempo.
            origem = Path.Combine(dir, $"ext_{chave}.src{Path.GetExtension(externa.Caminho).ToLowerInvariant()}");
            parcial = destino + ".tmp";
            await File.WriteAllTextAsync(origem, texto, new UTF8Encoding(false), ct);

            var psi = new ProcessStartInfo(_ffmpegPath);
            foreach (var arg in new[] { "-y", "-i", origem, "-f", "webvtt", parcial })
                psi.ArgumentList.Add(arg);

            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(1), null, _pararToken);
            if (exit != 0 || !File.Exists(parcial) || new FileInfo(parcial).Length == 0)
            {
                _logger.LogWarning("Conversão da legenda externa {Arquivo} do filme {Id} falhou (exit {Exit}): {Err}",
                    externa.Caminho, filmeId, exit, stderr);
                _falhas[destino] = DateTime.UtcNow;
                return null;
            }
            File.Move(parcial, destino, overwrite: true);
            return destino;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Não consegui ler a legenda externa {Arquivo} do filme {Id}.", externa.Caminho, filmeId);
            return null;
        }
        finally
        {
            ApagarSilencioso(origem);
            ApagarSilencioso(parcial);
            _extracao.Release();
        }
    }

    private bool FalhouRecentemente(string destino) =>
        _falhas.TryGetValue(destino, out var quando) && DateTime.UtcNow - quando < EsperaAposFalha;

    private static void ApagarSilencioso(string? arquivo)
    {
        try { if (arquivo is not null && File.Exists(arquivo)) File.Delete(arquivo); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
