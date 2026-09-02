using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Data;

/// <summary>
/// "Auto-migração" rodada a cada boot. O projeto não usa EF Migrations: <c>EnsureCreated()</c>
/// monta o schema num banco novo, e os blocos <c>ExecuteSqlRaw</c> abaixo — todos idempotentes —
/// evoluem um <c>/data/filmes.db</c> que já existe. O DDL cru espelha exatamente o que o EF
/// geraria (tipos <c>INTEGER</c>/<c>REAL</c>/<c>TEXT</c>, nomes de constraint por convenção),
/// pra um banco criado por <c>EnsureCreated()</c> e um "migrado na mão" convergirem.
/// </summary>
public static class DbInitializer
{
    public static void Executar(AppDbContext db)
    {
        db.Database.EnsureCreated();

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
        foreach (var (nome, ddl) in new (string Nome, string Ddl)[]
        {
            ("TmdbId", """ALTER TABLE "Filmes" ADD COLUMN "TmdbId" INTEGER NULL;"""),
            ("TituloOriginal", """ALTER TABLE "Filmes" ADD COLUMN "TituloOriginal" TEXT NULL;"""),
            ("PosterUrl", """ALTER TABLE "Filmes" ADD COLUMN "PosterUrl" TEXT NULL;"""),
            ("Sinopse", """ALTER TABLE "Filmes" ADD COLUMN "Sinopse" TEXT NULL;"""),
            ("MetadadosEm", """ALTER TABLE "Filmes" ADD COLUMN "MetadadosEm" TEXT NULL;"""),
        })
            if (!colunasFilmes.Contains(nome)) db.Database.ExecuteSqlRaw(ddl);
    }
}
