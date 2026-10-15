using System.Reflection;
using FilmesApi.Data;
using FilmesApi.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
builder.Services.AddSingleton<EstadoMontagens>();
builder.Services.AddScoped<FilmeService>();
builder.Services.AddScoped<ProgressoService>();
builder.Services.AddSingleton<RkmppCapabilityService>();
builder.Services.AddSingleton<ThermalService>();
builder.Services.AddSingleton<HlsTranscodeService>();
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddSingleton<PlayerStateService>();
builder.Services.AddHostedService<ScanPeriodicoService>();
builder.Services.AddHostedService<PreAquecerContinuarService>();
builder.Services.AddSingleton<NomesPersonalizadosService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NomesPersonalizadosService>());
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
    })
    .AddCheck<MontagensHealthCheck>("montagens", tags: [MontagensHealthCheck.Tag]);

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
// AdminToken (opcional): exige X-Admin-Token em /api/admin/* e no scan manual. Vazio = aberto, como sempre foi.
var adminToken = app.Configuration.GetValue<string>("AdminToken");
if (!string.IsNullOrEmpty(adminToken))
    app.Use(async (ctx, next) =>
    {
        if (TokenAdmin.Protegida(ctx.Request.Method, ctx.Request.Path)
            && !TokenAdmin.Autorizado(adminToken, ctx.Request.Headers[TokenAdmin.Header].FirstOrDefault()))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { mensagem = $"token de administrador ausente ou errado (header {TokenAdmin.Header})." });
            return;
        }
        await next();
    });

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
// /health: o que o Uptime Kuma olha — disco/pasta caída ("Degraded") vira 503 pra avisar. /health/vivo é o do
// healthcheck do Docker: nem roda o check das montagens (tag "montagens"), então disco caído NUNCA o deixa unhealthy
// (e nada reinicia por causa disso) — só 503 se o app estiver mesmo quebrado.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
});
app.MapHealthChecks("/health/vivo", new HealthCheckOptions { Predicate = c => !c.Tags.Contains(MontagensHealthCheck.Tag) });

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

// Relê o nomes.json (apelidos de série/nomes de exibição) na hora, sem esperar os 15 s do relógio.
app.MapPost("/api/admin/recarregar-nomes", (NomesPersonalizadosService nomes) =>
{
    var (resultado, arquivo) = nomes.Recarregar();
    return resultado == NomesPersonalizadosService.Resultado.Invalido
        ? Results.BadRequest(new { mensagem = "nomes.json inválido — veja o log do servidor; os nomes anteriores continuam valendo.", caminho = nomes.Caminho })
        : Results.Ok(new
        {
            resultado = resultado.ToString().ToLowerInvariant(),
            apelidos = arquivo.Contagem.Apelidos,
            nomes = arquivo.Contagem.Nomes,
            ignorar = arquivo.Contagem.Ignorar,
            tipos = arquivo.Contagem.Tipos,
        });
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
