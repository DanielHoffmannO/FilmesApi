using System.Text.Json.Serialization;
using FilmesApi.Data;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ─── Kestrel otimizado para ARM (Rock Pi) ───────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxResponseBufferSize = 65536;
    k.Limits.MinResponseDataRate = null; // TV pode pausar stream sem timeout
});

// ─── Serviços ───────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=/data/filmes.db"));

builder.Services.AddScoped<FilmeService>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS aberto pra qualquer dispositivo na LAN (TV, celular, etc)
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─── Auto-migrate ───────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// ─── Pipeline ───────────────────────────────────────────────────────────
app.UseCors();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI(c => c.RoutePrefix = "swagger");
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
