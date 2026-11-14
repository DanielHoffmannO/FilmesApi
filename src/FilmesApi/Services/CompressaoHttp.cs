using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;

namespace FilmesApi.Services;

/// <summary>
/// Compressão e cache HTTP. O middleware só olha o <c>Accept-Encoding</c> do cliente (a TV, Chromium 25, manda <c>gzip,deflate</c>, sem br;
/// cliente sem o header recebe o corpo cru) e só mexe em tipos de TEXTO listados em <see cref="Tipos"/>. Vídeo
/// (<c>.ts</c>/<c>.mp4</c>), playlist HLS (<c>.m3u8</c>) e legenda ficam de fora por tipo; resposta com <c>Content-Range</c> (206, Range) nunca é comprimida.
/// </summary>
public static class CompressaoHttp
{
    /// <summary>Só estes. Qualquer outro tipo (video/*, application/vnd.apple.mpegurl, text/vtt, application/octet-stream…) passa direto.</summary>
    public static readonly string[] Tipos =
    [
        "application/json", "text/html", "text/css",
        "text/javascript", "application/javascript", "application/x-javascript",
        "image/svg+xml",
    ];

    public static IServiceCollection AddCompressaoHttp(this IServiceCollection servicos)
    {
        servicos.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.MimeTypes = Tipos;                // substitui a lista padrão (que inclui text/plain, xml…)
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
        });
        // Fastest: a CPU do Rock Pi é fraca e o JSON do catálogo/hls.min.js só precisam ficar bem menores, não mínimos.
        servicos.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        servicos.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return servicos;
    }

    /// <summary>Cache-Control dos arquivos estáticos. html e <c>/js</c>, <c>/css</c> são nossos e mudam juntos: <c>no-cache</c> (revalida; o ETag
    /// do StaticFiles devolve 304 sem reenviar). <c>/vendor</c> (hls.min.js, ~500 KB) é de terceiro e não muda sozinho: um dia em cache sem revalidar.
    /// Resposta 304/206 e o resto ficam como estavam.</summary>
    public static void CacheDeEstaticos(StaticFileResponseContext ctx)
    {
        var caminho = ctx.Context.Request.Path;
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || caminho.StartsWithSegments("/js") || caminho.StartsWithSegments("/css"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        else if (caminho.StartsWithSegments("/vendor"))
            ctx.Context.Response.Headers.CacheControl = "public, max-age=86400";
    }
}
