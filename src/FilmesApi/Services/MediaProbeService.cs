using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FilmesApi.Services;

public record FaixaAudio(int Index, string? Codec, string? Idioma, bool Default);

/// <summary><c>IdxRelativo</c> = posição entre as faixas de legenda (0,1,2…), usada em
/// <c>-map 0:s:N</c> e no endpoint <c>/legenda/{idx}</c>.</summary>
public record FaixaLegenda(int IdxRelativo, string Codec, string? Idioma, string? Titulo, bool Forced, bool Default);

/// <summary><c>Video10Bit</c>: H.264/HEVC "High 10"/"Main 10" (pix_fmt tipo <c>yuv420p10le</c>
/// ou <c>bits_per_raw_sample</c> ≥ 9) — o navegador não decodifica, mesmo com codec_name
/// "compatível". Sem isso o arquivo tocava direto com tela preta e nenhum erro em lugar
/// nenhum (ver <see cref="HlsTranscodeService.ClassificarCompatibilidade"/>).</summary>
public record MediaInfo(
    string? VideoCodec, int Largura, int Altura, bool Video10Bit, double? DuracaoSegundos,
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

    /// <summary><c>internal</c> de propósito: dá pra testar o kill-no-timeout com um processo
    /// fake (<c>sleep</c>) sem precisar de ffprobe de verdade — ver <c>FilmesApi.Tests</c>.</summary>
    internal async Task<string?> RodarAsync(string[] args, TimeSpan timeout, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobe) { RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;
            var saida = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0 ? saida : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Sem matar, o ffprobe fica rodando sozinho (arquivo num mount travado é o caso
            // real) — cada timeout subsequente empilha mais um zumbi, e o servidor vai
            // ficando lento até reiniciar. Mesma lógica do Matar() do ProcessRunner.
            try { proc?.Kill(entireProcessTree: true); } catch { /* já morreu / sem permissão */ }
            _logger.LogWarning("ffprobe estourou {Seg}s para {Path} — abortado.", timeout.TotalSeconds, path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "ffprobe falhou ao iniciar para {Path}.", path);
            return null;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    /// <summary><c>internal</c> de propósito: dá pra testar o parsing do JSON do ffprobe
    /// (attached_pic, 10-bit…) sem rodar processo nenhum — mesma ideia do
    /// <see cref="HlsTranscodeService.MontarArgsFfmpegHls"/>.</summary>
    internal static MediaInfo Parsear(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? videoCodec = null;
        int largura = 0, altura = 0;
        var video10Bit = false;
        var audios = new List<FaixaAudio>();
        var legendas = new List<FaixaLegenda>();
        var idxLegenda = 0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var s in streams.EnumerateArray())
            {
                var tipo = Str(s, "codec_type");
                switch (tipo)
                {
                    // attached_pic (capa/poster embutido, ex.: rip de anime/música) não é o
                    // vídeo de verdade — sem esse filtro, um arquivo com capa antes da faixa
                    // real virava "vídeo" de 1 frame no codec da imagem (mjpeg/png).
                    case "video" when videoCodec is null && !Disp(s, "attached_pic"):
                        videoCodec = Str(s, "codec_name");
                        largura = Int(s, "width");
                        altura = Int(s, "height");
                        video10Bit = EhDezBits(s);
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

        return new MediaInfo(videoCodec, largura, altura, video10Bit, duracao, audios, legendas);
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

    // pix_fmt tipo "yuv420p10le"/"p010le" é o sinal mais confiável de profundidade de cor —
    // cobre H.264 High 10, HEVC Main 10 etc. bits_per_raw_sample (ffprobe manda como string)
    // é o fallback pros casos em que o nome do pix_fmt não deixa claro.
    private static bool EhDezBits(JsonElement s)
    {
        var pixFmt = Str(s, "pix_fmt");
        if (pixFmt is not null && (pixFmt.Contains("10") || pixFmt.Contains("12") || pixFmt.Contains("16")))
            return true;
        var bits = Str(s, "bits_per_raw_sample");
        return bits is not null && int.TryParse(bits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 9;
    }
}
