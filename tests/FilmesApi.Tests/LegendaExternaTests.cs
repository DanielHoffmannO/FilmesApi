using System.Diagnostics;
using System.Text;
using FilmesApi.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace FilmesApi.Tests;

/// <summary>
/// Legenda externa (.srt solto). Regressão real: "Modern Family" vinha com
/// <c>Legendas/01 - Pilot.srt</c> ao lado de <c>01 - Pilot.mp4</c> e a API só olhava faixa
/// embutida — o menu de CC ficava vazio. Além do achar-o-arquivo, o .srt era Windows-1252, e
/// lido como UTF-8 vira "a��o".
/// </summary>
public class LegendaExternaTests
{
    private readonly ITestOutputHelper _log;
    public LegendaExternaTests(ITestOutputHelper log) => _log = log;

    // ─── Casa: nome do vídeo × nome da legenda ───────────────────────────

    [Theory]
    [InlineData("01 - Pilot", "01 - Pilot", true)]
    [InlineData("23 - Hawaii", "23 -Hawaii", true)]                       // espaçamento diferente (Modern Family)
    [InlineData("Filme", "Filme.por", true)]                              // sufixo de idioma
    [InlineData("Filme", "FILME.pt-BR", true)]
    [InlineData("Ação Total", "acao total", true)]                        // acento/caixa
    [InlineData("Filme", "Filmes extras", false)]                         // prefixo, mas não fronteira de palavra
    [InlineData("01 - Pilot", "02 - The Bicycle Thief", false)]
    [InlineData("Futurama.S02E01.A.Flight.1080p", "Futurama S02E01 outro nome", true)]  // mesmo SxxEyy
    [InlineData("Show S01E01", "Show S01E02", false)]
    [InlineData("The Twilight Zone [1x01] Shatterday", "The Twilight Zone [1x01] Shatterday", true)]
    [InlineData("Filme 1920x1080", "Outro 1920x1080", false)]             // resolução não é episódio
    [InlineData("", "Filme", false)]
    public void Casa_reconhece(string video, string legenda, bool esperado) =>
        Assert.Equal(esperado, LegendaExternaLocalizador.Casa(video, legenda));

    [Theory]
    [InlineData("S02E05", 2, 5)]
    [InlineData("show.s1e12.720p", 1, 12)]
    [InlineData("Twilight [1x06-07] x", 1, 6)]
    public void ChaveEpisodio_le(string nome, int t, int e) =>
        Assert.Equal((t, e), LegendaExternaLocalizador.ChaveEpisodio(nome));

    [Fact]
    public void ChaveEpisodio_resolucao_nao_e_episodio() =>
        Assert.Null(LegendaExternaLocalizador.ChaveEpisodio("Filme 1920x1080 x264"));

    // ─── Idioma ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Filme", "Filme.pt-BR", "", "por")]
    [InlineData("Filme", "Filme.por", "", "por")]
    [InlineData("Filme", "Filme.eng", "", "eng")]
    [InlineData("Filme", "Filme", "eng_subs", "eng")]                     // vem da pasta
    [InlineData("Filme", "Filme", "Legendas", null)]
    [InlineData("Run for it", "Run for it", "", null)]                    // "it" é palavra do título, não italiano
    [InlineData("01 - Pilot", "01 - Pilot", "Legendas", null)]
    public void IdiomaDoNome_le(string video, string legenda, string pasta, string? esperado) =>
        Assert.Equal(esperado, LegendaExternaLocalizador.IdiomaDoNome(video, legenda, pasta));

    // ─── Decodificação ───────────────────────────────────────────────────

    [Fact]
    public void Decodifica_windows_1252_como_texto_correto()
    {
        // "ação" em Windows-1252: 0xE7 0xE3 — inválido como UTF-8.
        var bytes = new byte[] { 0x61, 0xE7, 0xE3, 0x6F, 0x20, 0x92, 0x20 };
        Assert.Equal("ação ’ ", LegendaExternaLocalizador.DecodificarTexto(bytes));
    }

    [Fact]
    public void Utf8_valido_nao_e_reinterpretado() =>
        Assert.Equal("ação", LegendaExternaLocalizador.DecodificarTexto(Encoding.UTF8.GetBytes("ação")));

    [Fact]
    public void Bom_utf8_e_removido() =>
        Assert.Equal("1", LegendaExternaLocalizador.DecodificarTexto([0xEF, 0xBB, 0xBF, 0x31]));

    [Fact]
    public void Utf16_com_bom_e_lido() =>
        Assert.Equal("ação", LegendaExternaLocalizador.DecodificarTexto(
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("ação")]));

    // ─── Localizar (com disco de verdade) ────────────────────────────────

    [Fact]
    public void Layout_modern_family_cada_episodio_acha_o_seu_srt()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "01 - Pilot.mp4"); Toca(dir, "23 - Hawaii.mp4"); Toca(dir, "24 - Family Portrait.mp4");
            Toca(dir, "Legendas", "01 - Pilot.srt"); Toca(dir, "Legendas", "23 -Hawaii.srt");
            Toca(dir, "Legendas", "24 - Family Portrait.srt");

            var pilot = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "01 - Pilot.mp4"));
            Assert.Single(pilot);
            Assert.EndsWith("01 - Pilot.srt", pilot[0].Caminho);
            Assert.Equal("Legenda", pilot[0].Titulo);

            var hawaii = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "23 - Hawaii.mp4"));
            Assert.Single(hawaii);
            Assert.EndsWith("23 -Hawaii.srt", hawaii[0].Caminho);
        }
        finally { Limpa(dir); }
    }

    [Fact]
    public void Srt_na_mesma_pasta_e_extensao_maiuscula()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "Show [1x01] Pilot.avi"); Toca(dir, "Show [1x01] Pilot.Srt");
            Toca(dir, "Show [1x02] Dois.avi");
            var r = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "Show [1x01] Pilot.avi"));
            Assert.Single(r);
            Assert.EndsWith("Pilot.Srt", r[0].Caminho);
            Assert.Empty(LegendaExternaLocalizador.Localizar(Path.Combine(dir, "Show [1x02] Dois.avi")));
        }
        finally { Limpa(dir); }
    }

    [Fact]
    public void Filme_unico_na_pasta_usa_legenda_de_nome_diferente()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "O Rei Leão (1994) 1080p.mp4");
            Toca(dir, "Legendas", "legenda qualquer.srt");
            var r = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "O Rei Leão (1994) 1080p.mp4"));
            Assert.Single(r);
            Assert.Equal("legenda qualquer", r[0].Titulo);
        }
        finally { Limpa(dir); }
    }

    [Fact]
    public void Varios_videos_sem_nome_casando_nao_recebem_legenda_alheia()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "a.mp4"); Toca(dir, "b.mp4");
            Toca(dir, "Legendas", "outra coisa.srt");
            Assert.Empty(LegendaExternaLocalizador.Localizar(Path.Combine(dir, "a.mp4")));
        }
        finally { Limpa(dir); }
    }

    [Fact]
    public void Legenda_principal_e_forcada_sao_diferenciadas_e_idioma_vem_do_sufixo()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "Star Wars 4K.mkv");
            Toca(dir, "Star Wars 4K.srt");
            Toca(dir, "LEGENDA FORÇADA", "Star Wars 4K.srt");
            Toca(dir, "Star Wars 4K.eng.srt");
            var r = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "Star Wars 4K.mkv"));
            Assert.Equal(3, r.Count);
            Assert.Contains(r, x => x.Forced && x.Titulo == "Legenda (forçada)");
            Assert.Contains(r, x => !x.Forced && x.Titulo == "Legenda");
            Assert.Contains(r, x => x.Idioma == "eng" && x.Titulo == "Inglês");
        }
        finally { Limpa(dir); }
    }

    [Fact]
    public void Dois_titulos_iguais_sao_numerados()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "Filme.mkv");
            Toca(dir, "Filme.pt.srt"); Toca(dir, "Filme.pt-BR.srt");
            var r = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "Filme.mkv"));
            Assert.Equal(["Português", "Português 2"], r.Select(x => x.Titulo));
        }
        finally { Limpa(dir); }
    }

    [Fact]
    public void Ordem_e_estavel_entre_chamadas()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "Filme.mkv");
            Toca(dir, "Filme.eng.srt"); Toca(dir, "Filme.por.srt"); Toca(dir, "Filme.spa.srt");
            var a = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "Filme.mkv")).Select(x => x.Caminho);
            var b = LegendaExternaLocalizador.Localizar(Path.Combine(dir, "Filme.mkv")).Select(x => x.Caminho);
            Assert.Equal(a, b);
        }
        finally { Limpa(dir); }
    }

    [Fact]
    public void Pasta_inexistente_ou_sem_legenda_devolve_vazio()
    {
        Assert.Empty(LegendaExternaLocalizador.Localizar("/nao/existe/x.mp4"));
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            Toca(dir, "x.mp4"); Toca(dir, "x.nfo");
            Assert.Empty(LegendaExternaLocalizador.Localizar(Path.Combine(dir, "x.mp4")));
        }
        finally { Limpa(dir); }
    }

    // ─── ponta a ponta: .srt Windows-1252 vira .vtt UTF-8 ────────────────

    [Fact]
    public async Task Srt_windows_1252_vira_vtt_utf8_com_indice_depois_das_embutidas()
    {
        var ffmpeg = AcharBinario("ffmpeg");
        var ffprobe = AcharBinario("ffprobe");
        if (ffmpeg is null || ffprobe is null) { _log.WriteLine("ffmpeg/ffprobe ausente — teste pulado"); return; }

        var dir = TestesComuns.CriarPastaTemp();
        var cache = TestesComuns.CriarPastaTemp();
        try
        {
            var video = Path.Combine(dir, "01 - Pilot.mp4");
            await Rodar(ffmpeg, $"-y -f lavfi -i testsrc=size=160x120:rate=5:duration=1 -c:v libx264 -pix_fmt yuv420p \"{video}\"");
            Directory.CreateDirectory(Path.Combine(dir, "Legendas"));
            var srt = "1\r\n00:00:00,100 --> 00:00:00,900\r\nNão é ação?\r\n";
            await File.WriteAllBytesAsync(Path.Combine(dir, "Legendas", "01 - Pilot.srt"),
                Encoding.GetEncoding(1252).GetBytes(srt));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["SubtitleCachePath"] = cache }).Build();
            var svc = new SubtitleService(new FfmpegOptions(ffmpeg, ffprobe), config,
                new MediaProbeService(new FfmpegOptions(ffmpeg, ffprobe), NullLogger<MediaProbeService>.Instance),
                NullLogger<SubtitleService>.Instance, new FimDaAplicacao());

            var faixas = await svc.ListarAsync(video, CancellationToken.None);
            var f = Assert.Single(faixas);
            Assert.Equal(0, f.Idx);          // sem embutidas, a externa começa em 0
            Assert.True(f.Convertivel);

            var vtt = await svc.ObterVttAsync(1, video, f.Idx, CancellationToken.None);
            Assert.NotNull(vtt);
            var texto = await File.ReadAllTextAsync(vtt!, Encoding.UTF8);
            Assert.StartsWith("WEBVTT", texto);
            Assert.Contains("Não é ação?", texto);

            // 2ª chamada vem do cache e devolve o mesmo arquivo
            Assert.Equal(vtt, await svc.ObterVttAsync(1, video, f.Idx, CancellationToken.None));
            // índice fora da faixa → null
            Assert.Null(await svc.ObterVttAsync(1, video, 5, CancellationToken.None));
        }
        finally { Limpa(dir); Limpa(cache); }
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static void Toca(string dir, params string[] partes)
    {
        var caminho = Path.Combine([dir, .. partes]);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(caminho, "x");
    }

    private static void Limpa(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string? AcharBinario(string nome)
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

    private static async Task Rodar(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);
    }

    private sealed class FimDaAplicacao : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
