using System.Net;
using System.Net.Http.Headers;
using FilmesApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary>Servidor real (Kestrel em porta livre) com a MESMA configuração de compressão/cache do app: comprime só texto e só se o cliente
/// pede; nunca vídeo, playlist HLS ou resposta de Range. A TV (Chromium 25, "gzip,deflate,sdch") usa o mesmo servidor.</summary>
public sealed class CompressaoHttpTests : IAsyncLifetime
{
    private static readonly string Grande = new('a', 20000);
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "http-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_raiz, "vendor"));
        Directory.CreateDirectory(Path.Combine(_raiz, "js"));
        File.WriteAllText(Path.Combine(_raiz, "vendor", "hls.min.js"), "var h=1;" + Grande);
        File.WriteAllText(Path.Combine(_raiz, "js", "util.js"), "var u=1;" + Grande);
        File.WriteAllText(Path.Combine(_raiz, "tela.html"), "<p>" + Grande + "</p>");
        File.WriteAllText(Path.Combine(_raiz, "video.ts"), Grande);   // conteúdo compressível, mas extensão de vídeo

        var b = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = _raiz, ContentRootPath = _raiz });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddCompressaoHttp();
        _app = b.Build();
        _app.UseResponseCompression();
        _app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = CompressaoHttp.CacheDeEstaticos });
        _app.MapGet("/api/dados", () => Results.Json(new { texto = Grande }));
        _app.MapGet("/hls/lista.m3u8", () => Results.Text("#EXTM3U\n" + Grande, "application/vnd.apple.mpegurl"));
        _app.MapGet("/api/mp4", () => Results.Bytes(System.Text.Encoding.ASCII.GetBytes(Grande), "video/mp4", enableRangeProcessing: true));
        _app.MapGet("/api/json-range", () => Results.Bytes(System.Text.Encoding.ASCII.GetBytes(Grande), "application/json", enableRangeProcessing: true));
        await _app.StartAsync();

        var url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        // Sem descompressão automática: o teste olha os bytes e cabeçalhos como a TV os veria.
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { BaseAddress = new Uri(url) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_raiz, true); } catch (IOException) { }
    }

    private Task<HttpResponseMessage> Pedir(string caminho, string? acceptEncoding, Action<HttpRequestMessage>? ajusta = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, caminho);
        if (acceptEncoding is not null) req.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        ajusta?.Invoke(req);
        return _http.SendAsync(req);
    }

    private static string? Codificacao(HttpResponseMessage r) => r.Content.Headers.ContentEncoding.FirstOrDefault();

    [Theory]
    [InlineData("/tela.html")]
    [InlineData("/js/util.js")]
    [InlineData("/vendor/hls.min.js")]
    [InlineData("/api/dados")]
    public async Task Texto_e_comprimido_quando_o_cliente_pede(string caminho)
    {
        var r = await Pedir(caminho, "gzip, deflate, br");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(Codificacao(r), new[] { "br", "gzip" });
        Assert.Contains("Accept-Encoding", r.Headers.Vary);
        Assert.True((await r.Content.ReadAsByteArrayAsync()).Length < 2000);
    }

    [Fact]
    public async Task Chromium_25_da_TV_recebe_gzip()
    {
        var r = await Pedir("/api/dados", "gzip,deflate,sdch");   // o Accept-Encoding dele, sem br
        Assert.Equal("gzip", Codificacao(r));
    }

    [Theory]
    [InlineData(null)]          // cliente que não manda o header
    [InlineData("identity")]
    [InlineData("")]
    public async Task Sem_pedir_nao_comprime_e_manda_o_corpo_cru(string? aceita)
    {
        foreach (var caminho in new[] { "/tela.html", "/js/util.js", "/api/dados", "/vendor/hls.min.js" })
        {
            var r = await Pedir(caminho, aceita);
            Assert.Null(Codificacao(r));
            Assert.True((await r.Content.ReadAsByteArrayAsync()).Length > 20000);
        }
    }

    [Theory]
    [InlineData("/video.ts")]
    [InlineData("/hls/lista.m3u8")]
    [InlineData("/api/mp4")]
    public async Task Video_e_playlist_nunca_sao_comprimidos(string caminho)
    {
        var r = await Pedir(caminho, "gzip, deflate, br");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(Codificacao(r));
        Assert.True((await r.Content.ReadAsByteArrayAsync()).Length >= 20000);
    }

    [Theory]
    [InlineData("/api/mp4")]
    [InlineData("/api/json-range")]   // até um tipo comprimível, com Range, sai cru: o Content-Range tem que bater com os bytes
    [InlineData("/video.ts")]
    public async Task Range_nunca_e_comprimido(string caminho)
    {
        var r = await Pedir(caminho, "gzip, deflate, br", q => q.Headers.Range = new RangeHeaderValue(100, 199));
        Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
        Assert.Null(Codificacao(r));
        Assert.Equal(100, (await r.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Cache_control_dos_estaticos()
    {
        Assert.Equal("no-cache", (await Pedir("/tela.html", "gzip")).Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", (await Pedir("/js/util.js", "gzip")).Headers.CacheControl?.ToString());
        var vendor = await Pedir("/vendor/hls.min.js", "gzip");
        Assert.Equal("public, max-age=86400", vendor.Headers.CacheControl?.ToString());
        Assert.NotNull(vendor.Headers.ETag);
    }

    [Fact]
    public async Task Revalidacao_com_etag_da_304_mesmo_comprimido()
    {
        var primeira = await Pedir("/vendor/hls.min.js", "gzip");
        var etag = primeira.Headers.ETag!.ToString();
        var segunda = await Pedir("/vendor/hls.min.js", "gzip", q => q.Headers.TryAddWithoutValidation("If-None-Match", etag));
        Assert.Equal(HttpStatusCode.NotModified, segunda.StatusCode);
    }
}
