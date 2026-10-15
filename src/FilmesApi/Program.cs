using System.Reflection;
using FilmesApi.Data;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ─── Kestrel otimizado para ARM (Rock Pi) ───────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxResponseBufferSize = 65536;
    k.Limits.MinResponseDataRate = null; // TV pode pausar stream sem timeout
});

// ─── Serviços ───────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=/data/filmes.db")
       .AddInterceptors(new SqlitePragmaInterceptor()));

builder.Services.AddSingleton(FfmpegOptions.From(builder.Configuration));
builder.Services.AddSingleton<MediaProbeService>();
builder.Services.AddSingleton<ClassificacaoCache>();
builder.Services.AddScoped<FilmeService>();
builder.Services.AddScoped<ProgressoService>();
builder.Services.AddSingleton<RkmppCapabilityService>();
builder.Services.AddSingleton<ThermalService>();
builder.Services.AddSingleton<HlsTranscodeService>();
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddSingleton<PlayerStateService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TmdbService>();
builder.Services.AddHostedService<MetadataService>();

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "FilmesApi",
        Version = "v1",
        Description = "Servidor pessoal de streaming — catálogo, transcode HLS sob demanda, "
                    + "legendas, retomada, próximo episódio e controle da TV pelo celular.",
    });
    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml);
});

// health check — o mount da pasta de mídia (SMB/USB) cai; o ffmpeg pode não estar no lugar.
builder.Services.AddHealthChecks()
    .AddCheck("media", () =>
    {
        var p = builder.Configuration.GetValue<string>("MediaPath") ?? "/media";
        return Directory.Exists(p) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"MediaPath '{p}' não está montado");
    })
    .AddCheck("ffmpeg", () =>
    {
        var ff = builder.Configuration.GetValue<string>("FfmpegPath") ?? "ffmpeg";
        return !Path.IsPathRooted(ff) || File.Exists(ff) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"ffmpeg '{ff}' não existe");
    });

// CORS: as páginas são same-origin. Só libera geral se AllowAnyOrigin=true (caso alguém sirva
// as telas de outro host); por padrão, sem CORS.
if (builder.Configuration.GetValue<bool>("AllowAnyOrigin"))
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─── Boot: auto-migração ────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    DbInitializer.Executar(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

// Poda do cache HLS fora do caminho de boot: varre até 20 GB de arquivos calculando
// tamanho, e em SD card isso segurava o start antes de aceitar a primeira request.
_ = Task.Run(() =>
{
    try { app.Services.GetRequiredService<HlsTranscodeService>().LimparCacheExcedente(); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Poda inicial do cache HLS falhou."); }
});

// ─── Pipeline ───────────────────────────────────────────────────────────
if (app.Configuration.GetValue<bool>("AllowAnyOrigin")) app.UseCors();
// index.html/tv.html/controle.html quase tudo inline, mas wwwroot/js/*.js e wwwroot/css/*.css
// (compartilhados entre as 3) são nossos e mudam junto — sem no-cache aqui, TV agressiva em
// cache pode continuar rodando um util.js/base.css velho mesmo depois de um F5 na página.
// vendor/hls.min.js é biblioteca de terceiro vendorizada, não muda sozinha: cache normal está bem.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || ctx.Context.Request.Path.StartsWithSegments("/js")
            || ctx.Context.Request.Path.StartsWithSegments("/css"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.UseSwagger();
app.UseSwaggerUI(c => c.RoutePrefix = "swagger");
app.MapControllers();
app.MapHealthChecks("/health");

// Diagnostico client-side: tv.html nao tem console acessivel (TV sem devtools) — manda
// erro de JS/hls.js/<video> pra cá, cai direto no docker logs. Sem persistir em banco:
// é só pra depuração pontual.
app.MapPost("/api/diag/log", (DiagLogRequest req, ILoggerFactory lf) =>
{
    // Sem autenticação (rede de casa): limita o tamanho pra um cliente bugado não inundar o log.
    var msg = req.Msg ?? "";
    lf.CreateLogger("TvDiag").LogWarning("[tv.html] {Msg}", msg.Length > 500 ? msg[..500] : msg);
    return Results.NoContent();
});

// TV Samsung antiga (Orsay): Menu Develop > IP Setting > Start App Sync baixa estes dois. O
// endereço do app vem do que a própria TV usou pra chegar aqui (Host), sem IP gravado em arquivo.
app.MapGet("/widgetlist.xml", (HttpContext ctx, IWebHostEnvironment env) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var (_, tamanho) = OrsayWidget.Gerar(Path.Combine(env.WebRootPath, "orsay-widget"), baseUrl);
    return Results.Content(OrsayWidget.ListaXml(baseUrl, tamanho), "application/xml");
});
app.MapGet(OrsayWidget.CaminhoZip, (HttpContext ctx, IWebHostEnvironment env) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var (zip, _) = OrsayWidget.Gerar(Path.Combine(env.WebRootPath, "orsay-widget"), baseUrl);
    return Results.File(zip, "application/zip");
});

app.MapFallbackToFile("index.html");

app.Run();

record DiagLogRequest(string Msg);
