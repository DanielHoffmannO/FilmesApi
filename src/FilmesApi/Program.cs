using System.Reflection;
using System.Text.Json.Serialization;
using FilmesApi.Data;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;

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
builder.Services.AddScoped<FilmeService>();
builder.Services.AddScoped<ProgressoService>();
builder.Services.AddSingleton<RkmppCapabilityService>();
builder.Services.AddSingleton<ThermalService>();
builder.Services.AddSingleton<HlsTranscodeService>();
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddSingleton<PlayerStateService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TmdbService>();
builder.Services.AddHostedService<PreTranscodeService>();
builder.Services.AddHostedService<MetadataService>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI(c => c.RoutePrefix = "swagger");
app.MapControllers();
app.MapHealthChecks("/health");
app.MapFallbackToFile("index.html");

app.Run();
