using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FilmesApi.Data;

/// <summary>
/// Roda os PRAGMAs de robustez em toda conexão SQLite recém-aberta. No Rock Pi o disco é
/// lento e o tick de progresso do player concorre com o <c>/scan</c> — sem isso o segundo
/// escritor toma "database is locked" na hora (ver os <c>catch (DbUpdateException)</c> em
/// ProgressoService/FilmeService).
///
/// <list type="bullet">
///   <item><c>journal_mode=WAL</c>: leitor não bloqueia escritor e vice-versa. Persiste no
///     arquivo .db, mas re-executar é barato (no-op se já está em WAL).</item>
///   <item><c>busy_timeout=5000</c>: espera até 5 s por um lock em vez de falhar na hora.
///     É por-conexão, então tem que ser aqui, não no DbInitializer.</item>
///   <item><c>synchronous=NORMAL</c>: seguro com WAL, bem menos fsync (importa em SD card).</item>
///   <item><c>foreign_keys=ON</c>: o ON DELETE CASCADE de Progressos só vale com isto.</item>
/// </list>
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Aplicar(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Aplicar(connection);
        return Task.CompletedTask;
    }

    private static void Aplicar(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            """;
        cmd.ExecuteNonQuery();
    }
}
