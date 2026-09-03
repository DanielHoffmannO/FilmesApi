using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FilmesApi.Services;

public record FaixaAudio(int Index, string? Codec, string? Idioma, bool Default);

/// <summary><c>IdxRelativo</c> = posição entre as faixas de legenda (0,1,2…), usada em
/// <c>-map 0:s:N</c> e no endpoint <c>/legenda/{idx}</c>.</summary>
public record FaixaLegenda(int IdxRelativo, string Codec, string? Idioma, string? Titulo, bool Forced, bool Default);

public record MediaInfo(
    string? VideoCodec, int Largura, int Altura, double? DuracaoSegundos,
    IReadOnlyList<FaixaAudio> Audios, IReadOnlyList<FaixaLegenda> Legendas);

/// <summary>
/// Uma única leitura de ffprobe (<c>-show_streams -show_format</c>) por arquivo, cacheada por
/// (caminho, mtime, tamanho). Antes o codec de vídeo, a resolução, a faixa de áudio e as
/// legendas eram 3–4 processos ffprobe espalhados por <see cref="HlsTranscodeService"/> e
/// <see cref="SubtitleService"/>, sem timeout e re-rodando a cada poll de status.
/// </summary>
public class MediaProbeService
{
    private readonly string _ffprobe;
    private readonly ILogger<MediaProbeService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Mtime, long Tamanho, MediaInfo Info)> _cache = new();

    public MediaProbeService(FfmpegOptions ffmpeg, ILogger<MediaProbeService> logger)
    {
        _ffprobe = ffmpeg.Ffprobe;
        _logger = logger;
    }

    /// <summary>Metadados do arquivo, ou null se o ffprobe falhou/estourou o timeout.</summary>
    public async Task<MediaInfo?> InspecionarAsync(string path, CancellationToken ct)
    {
        DateTime mtime;
        long tamanho;
        try { var fi = new FileInfo(path); mtime = fi.LastWriteTimeUtc; tamanho = fi.Length; }
        catch (IOException) { return null; }

        if (_cache.TryGetValue(path, out var c) && c.Mtime == mtime && c.Tamanho == tamanho)
            return c.Info;

        var json = await RodarAsync(
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path],
            TimeSpan.FromSeconds(30), path, ct);
        if (json is null) return null;

        try
        {
            var info = Parsear(json);
            _cache[path] = (mtime, tamanho, info);
            return info;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ffprobe: JSON inesperado para {Path}.", path);
            return null;
        }
    }

    private async Task<string?> RodarAsync(string[] args, TimeSpan timeout, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobe) { RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var saida = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0 ? saida : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("ffprobe estourou {Seg}s para {Path} — abortado.", timeout.TotalSeconds, path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "ffprobe falhou ao iniciar para {Path}.", path);
            return null;
        }
    }

    private static MediaInfo Parsear(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? videoCodec = null;
        int largura = 0, altura = 0;
        var audios = new List<FaixaAudio>();
        var legendas = new List<FaixaLegenda>();
        var idxLegenda = 0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var s in streams.EnumerateArray())
            {
                var tipo = Str(s, "codec_type");
                switch (tipo)
                {
                    case "video" when videoCodec is null:
                        videoCodec = Str(s, "codec_name");
                        largura = Int(s, "width");
                        altura = Int(s, "height");
                        break;
                    case "audio":
                        audios.Add(new FaixaAudio(Int(s, "index"), Str(s, "codec_name"),
                            Tag(s, "language"), Disp(s, "default")));
                        break;
                    case "subtitle":
                        legendas.Add(new FaixaLegenda(idxLegenda++, Str(s, "codec_name") ?? "",
                            Tag(s, "language"), Tag(s, "title"), Disp(s, "forced"), Disp(s, "default")));
                        break;
                }
            }

        double? duracao = null;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seg) && seg > 0)
            duracao = seg;

        return new MediaInfo(videoCodec, largura, altura, duracao, audios, legendas);
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string? Tag(JsonElement e, string tag) =>
        e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object ? Str(tags, tag) : null;

    private static bool Disp(JsonElement e, string flag) =>
        e.TryGetProperty("disposition", out var disp) && disp.ValueKind == JsonValueKind.Object
        && disp.TryGetProperty(flag, out var f) && f.ValueKind == JsonValueKind.Number && f.GetInt32() == 1;
}
