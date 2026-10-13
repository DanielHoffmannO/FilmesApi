using System.Diagnostics;
using System.Text;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>
/// Lista as faixas de legenda embutidas (via <see cref="MediaProbeService"/>) e extrai as de
/// texto pra WebVTT, servível como <c>&lt;track&gt;</c>. O HLS/stream direto continua sem
/// legenda muxada — isto é sidecar, gerado sob demanda.
///
/// Legenda bitmap (PGS/VobSub) não dá pra converter em texto — <c>Convertivel = false</c>.
///
/// Também acha legenda externa (<c>.srt</c> ao lado do vídeo ou numa subpasta
/// <c>Legendas</c>/<c>Subs</c>): entra na mesma lista, com índice depois das embutidas.
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

    private static readonly string[] PastasLegenda = ["legendas", "legenda", "subs", "subtitles"];

    /// <summary>Só letras/dígitos em minúsculo — "23 -Hawaii" e "23 - Hawaii" viram a mesma coisa.</summary>
    private static string Normalizar(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>.srt externos do vídeo, em ordem estável (o índice delas vem depois das
    /// embutidas). Casa pelo nome sem extensão, tolerando espaço/pontuação diferente e um
    /// sufixo de idioma (<c>Filme.pt.srt</c>).</summary>
    private static List<(string Caminho, string? Idioma)> AcharExternas(string arquivoAbsoluto)
    {
        var dir = Path.GetDirectoryName(arquivoAbsoluto);
        if (dir is null) return [];
        var alvo = Normalizar(Path.GetFileNameWithoutExtension(arquivoAbsoluto));
        if (alvo.Length == 0) return [];

        var pastas = new List<string> { dir };
        try
        {
            pastas.AddRange(Directory.EnumerateDirectories(dir)
                .Where(d => PastasLegenda.Contains(Path.GetFileName(d).ToLowerInvariant())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var achados = new List<(string, string?)>();
        foreach (var pasta in pastas)
        {
            IEnumerable<string> srts;
            try { srts = Directory.EnumerateFiles(pasta, "*.srt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var srt in srts)
            {
                var stem = Path.GetFileNameWithoutExtension(srt);
                string? idioma = null;
                var ok = Normalizar(stem) == alvo;
                if (!ok)
                {
                    var ponto = stem.LastIndexOf('.');
                    if (ponto > 0 && stem.Length - ponto - 1 is >= 2 and <= 5 && Normalizar(stem[..ponto]) == alvo)
                    {
                        ok = true;
                        idioma = stem[(ponto + 1)..].ToLowerInvariant();
                    }
                }
                if (ok) achados.Add((srt, idioma));
            }
        }
        return achados;
    }

    /// <summary>Faixas de legenda do arquivo. <c>Idx</c> é o índice relativo (0,1,2…) usado
    /// em <c>-map 0:s:N</c> e no endpoint <c>/legenda/{idx}</c>; as externas continuam a
    /// numeração depois das embutidas.</summary>
    public async Task<List<LegendaInfo>> ListarAsync(string arquivoAbsoluto, CancellationToken ct)
    {
        var info = await _probe.InspecionarAsync(arquivoAbsoluto, ct);
        var lista = (info?.Legendas ?? [])
            .Select(l => new LegendaInfo(l.IdxRelativo, l.Codec, l.Idioma, l.Titulo, l.Forced, l.Default,
                Convertivel: CodecsTexto.Contains(l.Codec)))
            .ToList();

        var proximo = lista.Count;
        foreach (var (caminho, idioma) in AcharExternas(arquivoAbsoluto))
            lista.Add(new LegendaInfo(proximo++, "srt", idioma, Path.GetFileName(caminho), false, false, true));
        return lista;
    }

    private static bool EhUtf8(string arquivo)
    {
        try { new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(arquivo)); return true; }
        catch (DecoderFallbackException) { return false; }
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
            var embutidas = (await _probe.InspecionarAsync(arquivoAbsoluto, ct))?.Legendas.Count ?? 0;
            string[] args;
            if (idxRelativo < embutidas)
                args = ["-y", "-i", arquivoAbsoluto, "-map", $"0:s:{idxRelativo}", "-c:s", "webvtt", "-f", "webvtt", destino];
            else
            {
                var srt = AcharExternas(arquivoAbsoluto)[idxRelativo - embutidas].Caminho;
                // .srt de release brasileira costuma vir em CP1252, não UTF-8.
                var charenc = EhUtf8(srt) ? Array.Empty<string>() : new[] { "-sub_charenc", "CP1252" };
                args = [.. charenc, "-y", "-i", srt, "-f", "webvtt", destino];
            }
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(3), null, _pararToken);
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
