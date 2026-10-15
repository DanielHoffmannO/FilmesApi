using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Tests;

/// <summary>A duração que o player manda é lixo em vários casos (stream ao vivo: Infinity;
/// metadado ainda não carregado: NaN/0) — nada disso pode virar "duração" no banco, senão a regra
/// de "perto do fim" passa a decidir com um número que não significa nada.</summary>
public class SalvarProgressoTests
{
    private static AppDbContext NovoDb()
    {
        var conexao = new SqliteConnection("Data Source=:memory:");
        conexao.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conexao).Options);
        db.Database.EnsureCreated();
        db.Filmes.Add(new Filme { Id = 1, Titulo = "X" });
        db.SaveChanges();
        return db;
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public async Task Duracao_invalida_nao_e_gravada(double duracao)
    {
        using var db = NovoDb();

        Assert.True(await new ProgressoService(db).SalvarAsync(1, posicao: 300, duracao));

        var p = await db.Progressos.AsNoTracking().SingleAsync();
        Assert.Equal(300, p.PosicaoSegundos);
        Assert.Null(p.DuracaoSegundos);
    }

    [Fact]
    public async Task Duracao_valida_e_gravada()
    {
        using var db = NovoDb();
        await new ProgressoService(db).SalvarAsync(1, posicao: 300, duracao: 2400);
        Assert.Equal(2400, (await db.Progressos.AsNoTracking().SingleAsync()).DuracaoSegundos);
    }

    [Fact]
    public async Task Duracao_invalida_em_save_seguinte_nao_apaga_a_valida()
    {
        using var db = NovoDb();
        var svc = new ProgressoService(db);
        await svc.SalvarAsync(1, 300, 2400);
        await svc.SalvarAsync(1, 400, double.NaN);

        var p = await db.Progressos.AsNoTracking().SingleAsync();
        Assert.Equal(400, p.PosicaoSegundos);
        Assert.Equal(2400, p.DuracaoSegundos);
    }

    [Fact]
    public async Task Filme_que_nao_existe_devolve_false()
    {
        using var db = NovoDb();
        Assert.False(await new ProgressoService(db).SalvarAsync(999, 300, 2400));
    }
}
