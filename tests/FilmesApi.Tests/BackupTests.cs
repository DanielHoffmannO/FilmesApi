using FilmesApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Backup do SQLite: snapshot via VACUUM INTO (inclusive o que ainda está no WAL), integrity_check, retenção dos N mais recentes.</summary>
public class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "backup-" + Guid.NewGuid().ToString("N"));
    private string Banco => Path.Combine(_dir, "filmes.db");
    private string Pasta => Path.Combine(_dir, "backups");

    public BackupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private BackupService Servico(int quantidade, Func<DateTime> agora, RegistroDeTarefas? t = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={Banco}",
            ["Backup:Pasta"] = Pasta,
            ["Backup:Quantidade"] = quantidade.ToString(),
        }).Build(), NullLogger<BackupService>.Instance, t, agora);

    private SqliteConnection AbrirBancoEmWal()
    {
        var con = new SqliteConnection($"Data Source={Banco};Pooling=False");
        con.Open();
        foreach (var sql in new[] { "PRAGMA journal_mode=WAL;", "PRAGMA wal_autocheckpoint=0;", "CREATE TABLE t(v TEXT);", "INSERT INTO t VALUES ('a'),('b'),('c');" })
        { using var c = con.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        return con;
    }

    [Fact]
    public async Task Snapshot_inclui_o_que_ainda_esta_so_no_wal_e_passa_no_integrity_check()
    {
        using var aberto = AbrirBancoEmWal();   // conexão aberta: as linhas estão no -wal, não no .db
        var tarefas = new RegistroDeTarefas();
        var r = await Servico(7, () => new DateTime(2026, 1, 2, 3, 0, 0), tarefas).ExecutarAsync();

        Assert.True(r.Ok, r.Erro);
        Assert.Equal("filmes-20260102-030000.db", r.Arquivo);
        Assert.Empty(Directory.GetFiles(Pasta, "*.tmp"));
        using var copia = new SqliteConnection($"Data Source={Path.Combine(Pasta, r.Arquivo!)};Mode=ReadOnly;Pooling=False");
        copia.Open();
        using var cmd = copia.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM t;";
        Assert.Equal(3L, cmd.ExecuteScalar());
        Assert.Equal("ok", Assert.Single(tarefas.Snapshot()).Estado);
    }

    [Fact]
    public async Task Retencao_mantem_so_os_N_mais_recentes()
    {
        using var aberto = AbrirBancoEmWal();
        var relogio = new DateTime(2026, 1, 1, 3, 0, 0);
        for (var i = 0; i < 5; i++)
        {
            var t = relogio.AddDays(i);
            Assert.True((await Servico(3, () => t).ExecutarAsync()).Ok);
        }
        Assert.Equal(["filmes-20260103-030000.db", "filmes-20260104-030000.db", "filmes-20260105-030000.db"],
            Directory.GetFiles(Pasta).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public void Retencao_nao_toca_em_arquivo_alheio_e_nunca_apaga_tudo()
    {
        Directory.CreateDirectory(Pasta);
        foreach (var n in new[] { "filmes-20260101-000000.db", "filmes-20260102-000000.db", "notas.txt" }) File.WriteAllText(Path.Combine(Pasta, n), "x");
        Assert.Equal(1, BackupService.AplicarRetencao(Pasta, 1));
        Assert.Equal(["filmes-20260102-000000.db", "notas.txt"], Directory.GetFiles(Pasta).Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.Equal(0, BackupService.AplicarRetencao(Pasta, 0));   // 0 vira 1: jamais zera os backups
    }

    [Fact]
    public async Task Banco_inexistente_falha_sem_lancar_e_sem_deixar_lixo()
    {
        var tarefas = new RegistroDeTarefas();
        var r = await Servico(7, () => new DateTime(2026, 1, 2, 3, 0, 0), tarefas).ExecutarAsync();
        Assert.False(r.Ok);
        Assert.Empty(Directory.GetFiles(Pasta, "filmes-*"));
        Assert.Equal("falhou", Assert.Single(tarefas.Snapshot()).Estado);
    }

    [Theory]
    [InlineData(2, 0, 3, 1)]       // 02:00 -> hoje 03:00
    [InlineData(3, 0, 3, 24)]      // 03:00:00 exatos -> amanhã
    [InlineData(23, 30, 3, 3.5)]   // 23:30 -> 03:00 de amanhã
    public void Proximo_horario(int h, int m, int hora, double esperadoHoras) =>
        Assert.Equal(TimeSpan.FromHours(esperadoHoras), BackupService.AteProximo(new DateTime(2026, 5, 5, h, m, 0), hora));
}
