using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Os dois pontos onde um nome vindo da URL vira caminho em disco (segmento HLS e remux
/// pronto): nada fora do cache pode sair daí, e só o que existe de verdade é devolvido.</summary>
public class CaminhosDeCacheTests : IDisposable
{
    private readonly string _raiz = TestesComuns.CriarPastaTemp();
    private readonly string _hls;
    private readonly string _remux;
    private readonly HlsTranscodeService _servico;

    public CaminhosDeCacheTests()
    {
        _hls = Path.Combine(_raiz, "hls");
        _remux = Path.Combine(_raiz, "remux");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HlsCachePath"] = _hls,
            ["RemuxCachePath"] = _remux,
        }).Build();
        var ffmpeg = new FfmpegOptions("ffmpeg-nao-usado", "ffprobe-nao-usado");
        _servico = new HlsTranscodeService(
            ffmpeg, config,
            new RkmppCapabilityService(ffmpeg, config, NullLogger<RkmppCapabilityService>.Instance),
            new ThermalService(config, NullLogger<ThermalService>.Instance),
            new MediaProbeService(ffmpeg, NullLogger<MediaProbeService>.Instance),
            NullLogger<HlsTranscodeService>.Instance,
            new LifetimeFalso());
    }

    public void Dispose() => Directory.Delete(_raiz, recursive: true);

    private string CriarSegmento(int filmeId, string nome)
    {
        var dir = Path.Combine(_hls, filmeId.ToString());
        Directory.CreateDirectory(dir);
        var caminho = Path.Combine(dir, nome);
        File.WriteAllText(caminho, "x");
        return caminho;
    }

    [Fact]
    public void Segmento_existente_com_nome_valido_e_devolvido()
    {
        var esperado = CriarSegmento(7, "seg_00012.ts");
        Assert.Equal(esperado, _servico.ObterCaminhoSegmento(7, "seg_00012.ts"));
    }

    [Fact]
    public void Segmento_valido_que_nao_existe_devolve_null() =>
        Assert.Null(_servico.ObterCaminhoSegmento(7, "seg_00099.ts"));

    [Theory]
    [InlineData("../seg_00001.ts")]
    [InlineData("..\\seg_00001.ts")]
    [InlineData("/etc/passwd")]
    [InlineData("seg_1.ts")]                  // menos de 5 dígitos
    [InlineData("seg_000001.ts")]             // mais de 5 dígitos
    [InlineData("seg_00001.ts\n")]            // '$' aceitaria a quebra de linha final
    [InlineData("seg_٠٠٠٠١.ts")]              // dígitos árabe-índicos: \d do .NET aceitaria
    [InlineData("playlist.m3u8")]
    [InlineData("")]
    public void Nome_que_nao_e_segmento_devolve_null_mesmo_com_arquivo_la(string nome)
    {
        CriarSegmento(7, "seg_00001.ts");
        // Cria também um arquivo com o próprio nome torto (quando o sistema de arquivos deixa):
        // sem isso o teste passaria só porque o arquivo não existe, sem provar que o nome foi recusado.
        if (nome.Length > 0 && nome.IndexOfAny(['/', '\\']) < 0) CriarSegmento(7, nome);
        Assert.Null(_servico.ObterCaminhoSegmento(7, nome));
    }

    [Fact]
    public void Remux_pronto_so_quando_o_mp4_completo_existe()
    {
        Assert.Null(_servico.ObterRemuxPronto(3));

        File.WriteAllText(_servico.CaminhoRemux(3) + ".tmp", "parcial");   // em andamento não conta
        Assert.Null(_servico.ObterRemuxPronto(3));

        File.WriteAllText(_servico.CaminhoRemux(3), "completo");
        Assert.Equal(_servico.CaminhoRemux(3), _servico.ObterRemuxPronto(3));
    }

    [Fact]
    public void Remux_pronto_marca_o_acesso_pro_lru()
    {
        File.WriteAllText(_servico.CaminhoRemux(3), "completo");
        var velho = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(_servico.CaminhoRemux(3), velho);

        _servico.ObterRemuxPronto(3);

        Assert.True(File.GetLastWriteTimeUtc(_servico.CaminhoRemux(3)) > velho.AddHours(4));
    }

    private sealed class LifetimeFalso : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
