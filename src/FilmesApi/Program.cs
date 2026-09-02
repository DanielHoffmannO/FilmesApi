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
builder.Services.AddSwaggerGen();

// CORS aberto pra qualquer dispositivo na LAN (TV, celular, etc)
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─── Auto-migrate ───────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated() só cria o banco do zero — não altera um /data/filmes.db que já existe.
    // Enquanto o projeto não migra pra EF Migrations (ver roadmap), garante a tabela nova
    // de forma idempotente pros bancos antigos. Tipos batem com o que o EF geraria (REAL/TEXT).
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Progressos" (
            "FilmeId" INTEGER NOT NULL CONSTRAINT "PK_Progressos" PRIMARY KEY,
            "PosicaoSegundos" REAL NOT NULL,
            "DuracaoSegundos" REAL NULL,
            "AtualizadoEm" TEXT NOT NULL,
            CONSTRAINT "FK_Progressos_Filmes_FilmeId" FOREIGN KEY ("FilmeId")
                REFERENCES "Filmes" ("Id") ON DELETE CASCADE
        );
        """);

    // Bancos antigos podem ter ArquivoPath duplicado (scan rodado 2x antes do índice único).
    // Remove as duplicatas (mantém o menor Id; o FK em cascata leva o progresso junto) e
    // então cria o índice — mesmo nome/definição que o EnsureCreated de um banco novo gera.
    db.Database.ExecuteSqlRaw("""
        DELETE FROM "Filmes"
        WHERE "ArquivoPath" IS NOT NULL AND "Id" NOT IN (
            SELECT MIN("Id") FROM "Filmes" WHERE "ArquivoPath" IS NOT NULL GROUP BY "ArquivoPath"
        );
        """);
    db.Database.ExecuteSqlRaw(
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Filmes_ArquivoPath" ON "Filmes" ("ArquivoPath");""");

    // Colunas de metadados do TMDB — SQLite não tem "ADD COLUMN IF NOT EXISTS", então
    // checa o pragma e adiciona só as que faltam (bancos antigos não têm nenhuma).
    var colunasFilmes = db.Database
        .SqlQueryRaw<string>("""SELECT name AS "Value" FROM pragma_table_info('Filmes');""").ToList();
    foreach (var (nome, ddl) in new (string, string)[]
    {
        ("TmdbId", """ALTER TABLE "Filmes" ADD COLUMN "TmdbId" INTEGER NULL;"""),
        ("TituloOriginal", """ALTER TABLE "Filmes" ADD COLUMN "TituloOriginal" TEXT NULL;"""),
        ("PosterUrl", """ALTER TABLE "Filmes" ADD COLUMN "PosterUrl" TEXT NULL;"""),
        ("Sinopse", """ALTER TABLE "Filmes" ADD COLUMN "Sinopse" TEXT NULL;"""),
        ("MetadadosEm", """ALTER TABLE "Filmes" ADD COLUMN "MetadadosEm" TEXT NULL;"""),
    })
        if (!colunasFilmes.Contains(nome)) db.Database.ExecuteSqlRaw(ddl);

    // Poda o cache HLS que passou do teto (ex.: acumulado por versões sem eviction).
    scope.ServiceProvider.GetRequiredService<HlsTranscodeService>().LimparCacheExcedente();
}

// ─── Pipeline ───────────────────────────────────────────────────────────
app.UseCors();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI(c => c.RoutePrefix = "swagger");
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
