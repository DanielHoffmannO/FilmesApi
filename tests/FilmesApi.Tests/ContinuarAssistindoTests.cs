using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão real: assistir um episódio no celular (index.html), pausar sem fechar o player,
/// terminar o MESMO episódio na TV (marca assistido, apaga o progresso) -- se o celular (aba
/// ainda aberta em segundo plano) mandar mais um save de progresso depois disso (típico no
/// 'pagehide', ao finalmente fechar a aba), isso reinseria uma linha de Progresso com a posição
/// VELHA do celular, e o episódio (já visto de verdade) reaparecia em "continuar assistindo"
/// como se não tivesse terminado.
/// </summary>
public class ContinuarAssistindoTests
{
    // SQLite real em memória (não o provider EF InMemory) -- mesmo motor de banco da produção,
    // então FK/índice único etc. se comportam igual.
    private static AppDbContext NovoDb()
    {
        var conexao = new SqliteConnection("Data Source=:memory:");
        conexao.Open();
        var opcoes = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conexao).Options;
        var db = new AppDbContext(opcoes);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task Progresso_tardio_de_filme_ja_assistido_nao_aparece_em_continuar()
    {
        using var db = NovoDb();
        db.Filmes.Add(new Filme { Id = 1, Titulo = "Episodio X", Assistido = true });
        db.Progressos.Add(new ProgressoReproducao
        {
            FilmeId = 1,
            PosicaoSegundos = 300,
            DuracaoSegundos = 2400,
            AtualizadoEm = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var lista = await new ProgressoService(db).ContinuarAssistindoAsync();

        Assert.Empty(lista);
    }

    [Fact]
    public async Task Progresso_de_filme_nao_assistido_continua_aparecendo()
    {
        using var db = NovoDb();
        db.Filmes.Add(new Filme { Id = 1, Titulo = "Episodio X", Assistido = false });
        db.Progressos.Add(new ProgressoReproducao
        {
            FilmeId = 1,
            PosicaoSegundos = 300,
            DuracaoSegundos = 2400,
            AtualizadoEm = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var lista = await new ProgressoService(db).ContinuarAssistindoAsync();

        var item = Assert.Single(lista);
        Assert.Equal(1, item.Id);
    }

    // Botão "x" em "continuar assistindo" (index.html): some da lista, mas diferente de
    // ConcluirAsync, NÃO marca como assistido -- só descarta o ponto de retomada.
    [Fact]
    public async Task RemoverProgresso_tira_da_lista_sem_marcar_assistido()
    {
        using var db = NovoDb();
        db.Filmes.Add(new Filme { Id = 1, Titulo = "Episodio X", Assistido = false });
        db.Progressos.Add(new ProgressoReproducao
        {
            FilmeId = 1,
            PosicaoSegundos = 300,
            DuracaoSegundos = 2400,
            AtualizadoEm = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var sucesso = await new ProgressoService(db).RemoverProgressoAsync(1);

        Assert.True(sucesso);
        Assert.Empty(await new ProgressoService(db).ContinuarAssistindoAsync());
        Assert.False((await db.Filmes.FindAsync(1))!.Assistido);
    }

    [Fact]
    public async Task RemoverProgresso_filme_inexistente_devolve_false()
    {
        using var db = NovoDb();

        Assert.False(await new ProgressoService(db).RemoverProgressoAsync(999));
    }

    [Fact]
    public async Task RemoverProgresso_sem_progresso_nenhum_e_idempotente()
    {
        using var db = NovoDb();
        db.Filmes.Add(new Filme { Id = 1, Titulo = "Episodio X", Assistido = false });
        await db.SaveChangesAsync();

        Assert.True(await new ProgressoService(db).RemoverProgressoAsync(1));
    }
}
