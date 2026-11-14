using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FilmesApi.Services;

/// <summary>Opções do backup do SQLite. <c>Backup:Pasta</c> (padrão: <c>backups</c> ao lado do .db), <c>Backup:Quantidade</c>
/// (quantos manter; padrão 7; <c>0</c> desliga o backup), <c>Backup:Hora</c> (hora local do backup diário, 0-23; padrão 3).</summary>
public sealed record BackupOptions(string Pasta, int Quantidade, int Hora)
{
    public bool Ligado => Quantidade > 0;

    public static BackupOptions From(IConfiguration config, string arquivoBanco)
    {
        var pasta = config["Backup:Pasta"];
        if (string.IsNullOrWhiteSpace(pasta))
            pasta = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(arquivoBanco)) ?? ".", "backups");
        return new BackupOptions(pasta, config.GetValue<int?>("Backup:Quantidade") ?? 7,
            Math.Clamp(config.GetValue<int?>("Backup:Hora") ?? 3, 0, 23));
    }
}

public sealed record ResultadoBackup(bool Ok, string? Arquivo, long Bytes, int Removidos, string? Erro);

/// <summary>
/// Backup diário do SQLite. Usa <c>VACUUM INTO</c> (snapshot consistente mesmo com o banco em WAL e em uso — copiar o
/// .db "no braço" perderia o que ainda está no -wal) num arquivo <c>.tmp</c>, roda <c>PRAGMA integrity_check</c> NO snapshot e só então renomeia
/// pra <c>filmes-AAAAMMDD-HHMMSS.db</c>. Mantém os N mais recentes. O banco vem do mesmo <c>ConnectionStrings:Default</c> que o app usa.
/// </summary>
public sealed class BackupService : BackgroundService
{
    public const string Tarefa = "backup";
    private const string Prefixo = "filmes-", Extensao = ".db", Temp = ".tmp";

    private readonly string _connectionString;
    private readonly string? _arquivoBanco;
    private readonly BackupOptions _op;
    private readonly RegistroDeTarefas? _tarefas;
    private readonly ILogger<BackupService> _log;
    private readonly Func<DateTime> _agora;
    private readonly SemaphoreSlim _trava = new(1, 1);

    public BackupService(IConfiguration config, ILogger<BackupService> log, RegistroDeTarefas? tarefas = null, Func<DateTime>? agora = null)
    {
        _connectionString = config.GetConnectionString("Default") ?? "Data Source=/data/filmes.db";
        var ds = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        _arquivoBanco = string.IsNullOrEmpty(ds) || ds.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ? null : ds;
        _op = BackupOptions.From(config, _arquivoBanco ?? "/data/filmes.db");
        _tarefas = tarefas;
        _log = log;
        _agora = agora ?? (() => DateTime.Now);
    }

    public BackupOptions Opcoes => _op;

    /// <summary><c>POST /api/admin/backup</c>: 400 se desligado, 409 se já há um rodando, 500 se falhou, 200 com o arquivo gerado.</summary>
    internal static async Task<IResult> Endpoint(BackupService backup, CancellationToken ct)
    {
        if (!backup.Opcoes.Ligado) return Results.BadRequest(new { mensagem = "backup desligado (Backup:Quantidade = 0)." });
        var r = await backup.ExecutarAsync(ct);
        if (r.Ok) return Results.Ok(new { arquivo = r.Arquivo, bytes = r.Bytes, removidos = r.Removidos, pasta = backup.Opcoes.Pasta });
        var emAndamento = r.Erro?.StartsWith("já existe um backup", StringComparison.Ordinal) == true;
        return Results.Json(new { mensagem = r.Erro }, statusCode: emAndamento ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError);
    }

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        if (!_op.Ligado || _arquivoBanco is null) { _tarefas?.Declarar(Tarefa, ligada: false); return; }
        _tarefas?.Declarar(Tarefa);
        try
        {
            while (!parar.IsCancellationRequested)
            {
                await Task.Delay(AteProximo(_agora(), _op.Hora), parar);
                await ExecutarAsync(parar);
            }
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Quanto falta até a próxima <paramref name="hora"/> cheia (sempre no futuro: às 03:00:00 exatos, é amanhã).</summary>
    internal static TimeSpan AteProximo(DateTime agora, int hora)
    {
        var alvo = agora.Date.AddHours(hora);
        if (alvo <= agora) alvo = alvo.AddDays(1);
        return alvo - agora;
    }

    /// <summary>Um backup; nunca lança. Duas chamadas ao mesmo tempo: a segunda recebe erro em vez de esperar.</summary>
    public async Task<ResultadoBackup> ExecutarAsync(CancellationToken ct = default)
    {
        if (!_op.Ligado || _arquivoBanco is null)
            return new ResultadoBackup(false, null, 0, 0, "backup desligado (Backup:Quantidade = 0) ou banco em memória");
        if (!await _trava.WaitAsync(0, ct))
            return new ResultadoBackup(false, null, 0, 0, "já existe um backup em andamento");

        var medicao = _tarefas?.Iniciar(Tarefa);
        try
        {
            var r = await Task.Run(Fazer, ct);
            if (r.Ok) medicao?.Ok($"{r.Arquivo} ({r.Bytes / 1048576.0:F1} MB)" + (r.Removidos > 0 ? $", {r.Removidos} antigo(s) apagado(s)" : ""));
            else { medicao?.Falhou(new InvalidOperationException(r.Erro)); _log.LogWarning("Backup do SQLite falhou: {Erro}", r.Erro); }
            return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            medicao?.Falhou(ex);
            _log.LogWarning(ex, "Backup do SQLite falhou.");
            return new ResultadoBackup(false, null, 0, 0, ex.Message);
        }
        finally { _trava.Release(); }
    }

    private ResultadoBackup Fazer()
    {
        Directory.CreateDirectory(_op.Pasta);
        foreach (var velho in Directory.EnumerateFiles(_op.Pasta, Prefixo + "*" + Temp)) TentarApagar(velho);   // sobra de um backup interrompido

        // O snapshot tem no máximo o tamanho do banco; exige o dobro de folga pra não encher o disco do app.
        var tamanhoBanco = File.Exists(_arquivoBanco!) ? new FileInfo(_arquivoBanco!).Length : 0;
        long? livre = null;
        try { livre = new DriveInfo(Path.GetFullPath(_op.Pasta)).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        if (livre is { } l && l < tamanhoBanco * 2)
            return new ResultadoBackup(false, null, 0, 0, $"pouco espaço livre em {_op.Pasta} ({l / 1048576} MB; o banco tem {tamanhoBanco / 1048576} MB)");

        var nome = Prefixo + _agora().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Extensao;
        var destino = Path.Combine(_op.Pasta, nome);
        var temp = destino + Temp;
        if (File.Exists(destino)) return new ResultadoBackup(false, null, 0, 0, "já existe backup com este nome (mesmo segundo)");

        try
        {
            // Pooling off: a conexão fecha de verdade, sem segurar o arquivo do banco depois.
            var cs = new SqliteConnectionStringBuilder(_connectionString) { Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString();
            using (var con = new SqliteConnection(cs))
            {
                con.Open();
                using (var busy = con.CreateCommand()) { busy.CommandText = "PRAGMA busy_timeout=10000;"; busy.ExecuteNonQuery(); }
                using var cmd = con.CreateCommand();
                cmd.CommandText = "VACUUM INTO $destino;";   // parâmetro: caminho com apóstrofo não quebra o SQL
                cmd.Parameters.AddWithValue("$destino", temp);
                cmd.ExecuteNonQuery();
            }

            var integridade = Integridade(temp);
            if (integridade != "ok") { TentarApagar(temp); return new ResultadoBackup(false, null, 0, 0, "integrity_check do snapshot: " + integridade); }

            File.Move(temp, destino);
        }
        catch { TentarApagar(temp); throw; }

        var removidos = AplicarRetencao(_op.Pasta, _op.Quantidade);
        return new ResultadoBackup(true, nome, new FileInfo(destino).Length, removidos, null);
    }

    private static string Integridade(string arquivo)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = arquivo, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var con = new SqliteConnection(cs);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "sem resposta";
    }

    /// <summary>Apaga os backups além dos <paramref name="manter"/> mais recentes (o nome tem a data, então ordenar por nome = por data).
    /// Só mexe em <c>filmes-*.db</c>; devolve quantos apagou.</summary>
    internal static int AplicarRetencao(string pasta, int manter)
    {
        var antigos = Directory.EnumerateFiles(pasta, Prefixo + "*" + Extensao)
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Math.Max(manter, 1)).ToList();
        foreach (var f in antigos) TentarApagar(f);
        return antigos.Count;
    }

    /// <summary>Resumo pro <c>/api/status</c>: quantos backups existem, o mais recente e o tamanho total.</summary>
    public object Resumo()
    {
        var arquivos = Directory.Exists(_op.Pasta)
            ? Directory.EnumerateFiles(_op.Pasta, Prefixo + "*" + Extensao).Select(f => new FileInfo(f)).OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : [];
        return new
        {
            ligado = _op.Ligado && _arquivoBanco is not null,
            pasta = _op.Pasta,
            manter = _op.Quantidade,
            hora = _op.Hora,
            quantidade = arquivos.Count,
            maisRecente = arquivos.FirstOrDefault()?.Name,
            maisRecenteEm = arquivos.FirstOrDefault()?.LastWriteTimeUtc,
            bytes = arquivos.Sum(f => f.Length),
        };
    }

    private static void TentarApagar(string f) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
