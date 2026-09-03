using System.Diagnostics;
using System.Text.Json;
using FilmesApi.Services;
using Xunit.Abstractions;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão: áudio 5.1/7.1 TEM que sair estéreo do HLS. AAC multicanal sem channel_layout
/// reconhecido é rejeitado EM SILÊNCIO pelo decoder do Chrome (MSE/hls.js) — a API responde
/// 200, os segmentos existem, e o vídeo simplesmente não toca. Já sumiu num refactor uma vez.
/// </summary>
public class HlsAudioDownmixTests
{
    private readonly ITestOutputHelper _log;
    public HlsAudioDownmixTests(ITestOutputHelper log) => _log = log;

    // ─── nível de argumento (rápido, sempre roda) ────────────────────────

    [Fact]
    public void Reencode_com_audio_sempre_forca_ac_2()
    {
        var args = HlsTranscodeService.MontarArgsFfmpegHls(
            "/media/x.mkv", videoCompativel: false, usarRkmpp: false,
            audioStreamIndex: 1, downscalePara: null, decodeHw: false);

        Assert.Contains("aac", ParDepoisDe(args, "-c:a"));
        Assert.Equal("2", ParDepoisDe(args, "-ac"));
    }

    [Fact]
    public void Remux_de_video_ainda_reencoda_audio_pra_estereo()
    {
        // -c:v copy, mas o áudio nunca é copiado: 5.1 no container original
        // continuaria 5.1 e quebraria o mesmo jeito.
        var args = HlsTranscodeService.MontarArgsFfmpegHls(
            "/media/x.mkv", videoCompativel: true, usarRkmpp: false,
            audioStreamIndex: 0, downscalePara: null, decodeHw: false);

        Assert.Equal("copy", ParDepoisDe(args, "-c:v"));
        Assert.Equal("aac", ParDepoisDe(args, "-c:a"));
        Assert.Equal("2", ParDepoisDe(args, "-ac"));
    }

    [Fact]
    public void Sem_faixa_de_audio_nao_passa_flags_de_audio()
    {
        var args = HlsTranscodeService.MontarArgsFfmpegHls(
            "/media/mudo.mkv", videoCompativel: false, usarRkmpp: false,
            audioStreamIndex: null, downscalePara: null, decodeHw: false);

        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-ac", args);
    }

    // ─── ponta a ponta: gera 5.1 de verdade, transcoda, mede a saída ──────

    [Fact]
    public async Task Fonte_5_1_vira_segmento_estereo()
    {
        var ffmpeg = AcharFfmpeg("ffmpeg");
        var ffprobe = AcharFfmpeg("ffprobe");
        if (ffmpeg is null || ffprobe is null) { _log.WriteLine("ffmpeg/ffprobe ausente — teste pulado"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "filmesapi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // fonte: 1s de vídeo h264 + áudio 5.1 em EAC3 (o caso do WEB-DL)
            var fonte = Path.Combine(dir, "fonte.mkv");
            await Rodar(ffmpeg,
                "-y -f lavfi -i testsrc=size=320x240:rate=15:duration=1 " +
                "-f lavfi -i sine=frequency=440:duration=1 -af aformat=channel_layouts=5.1 " +
                "-c:v libx264 -pix_fmt yuv420p -c:a eac3 -shortest " + Cita(fonte));

            Assert.Equal(6, await Canais(ffprobe, fonte));  // sanity: a fonte é mesmo 5.1

            // transcoda com os args reais do serviço
            var outDir = Path.Combine(dir, "hls");
            Directory.CreateDirectory(outDir);
            var args = HlsTranscodeService.MontarArgsFfmpegHls(
                fonte, videoCompativel: false, usarRkmpp: false,
                audioStreamIndex: 1, downscalePara: null, decodeHw: false);
            var (code, err) = await Rodar(ffmpeg, string.Join(' ', args.Select(Cita)), outDir);
            Assert.True(code == 0, $"ffmpeg saiu {code}: {err}");

            var seg = Directory.GetFiles(outDir, "seg_*.ts").OrderBy(x => x).First();
            Assert.Equal(2, await Canais(ffprobe, seg));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static string ParDepoisDe(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : "";
    }

    private static string Cita(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;

    private static string? AcharFfmpeg(string nome)
    {
        foreach (var p in new[] { $"/usr/bin/{nome}", $"/usr/local/bin/{nome}", $"/usr/lib/jellyfin-ffmpeg/{nome}", nome })
        {
            try
            {
                var psi = new ProcessStartInfo(p, "-version") { RedirectStandardOutput = true, RedirectStandardError = true };
                using var proc = Process.Start(psi);
                if (proc is null) continue;
                proc.WaitForExit(3000);
                if (proc.ExitCode == 0) return p;
            }
            catch { /* tenta o próximo */ }
        }
        return null;
    }

    private static async Task<(int Code, string Err)> Rodar(string exe, string args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        using var proc = Process.Start(psi)!;
        var err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, err);
    }

    private static async Task<int> Canais(string ffprobe, string arquivo)
    {
        var psi = new ProcessStartInfo(ffprobe,
            $"-v quiet -select_streams a:0 -show_entries stream=channels -of json {Cita(arquivo)}")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        var json = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("streams")[0].GetProperty("channels").GetInt32();
    }
}
