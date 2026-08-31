using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

public class FilmeService
{
    private readonly AppDbContext _db;
    private readonly HlsTranscodeService _transcode;
    private readonly string _mediaPath;
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm"];

    public FilmeService(AppDbContext db, HlsTranscodeService transcode, IConfiguration config)
    {
        _db = db;
        _transcode = transcode;
        _mediaPath = config.GetValue<string>("MediaPath") ?? "/media";
    }

    // Projeção compartilhada — inclui o ponto de retomada (LEFT JOIN em Progressos).
    private static readonly System.Linq.Expressions.Expression<Func<Filme, FilmeResponse>> ToResponse =
        f => new FilmeResponse(
            f.Id, f.Titulo, f.AnoLancamento, f.Diretor, f.ArquivoPath, f.Assistido, f.DataAdicionado,
            f.Progresso != null ? f.Progresso.PosicaoSegundos : (double?)null,
            f.Progresso != null ? f.Progresso.DuracaoSegundos : null);

    public async Task<List<FilmeResponse>> ListarAsync(bool? assistido = null)
    {
        var query = _db.Filmes.AsNoTracking().AsQueryable();
        if (assistido.HasValue) query = query.Where(f => f.Assistido == assistido.Value);

        return await query.OrderByDescending(f => f.DataAdicionado)
            .Select(ToResponse)
            .ToListAsync();
    }

    public async Task<FilmeResponse?> ObterAsync(int id)
    {
        return await _db.Filmes.AsNoTracking()
            .Where(f => f.Id == id)
            .Select(ToResponse)
            .FirstOrDefaultAsync();
    }

    public async Task<FilmeResponse> CriarAsync(FilmeRequest req)
    {
        var filme = new Filme
        {
            Titulo = req.Titulo,
            AnoLancamento = req.AnoLancamento,
            Diretor = req.Diretor,
            ArquivoPath = req.ArquivoPath
        };
        _db.Filmes.Add(filme);
        await _db.SaveChangesAsync();
        return new FilmeResponse(filme.Id, filme.Titulo, filme.AnoLancamento, filme.Diretor, filme.ArquivoPath,
            filme.Assistido, filme.DataAdicionado, null, null);
    }

    public async Task<bool> MarcarAssistidoAsync(int id)
    {
        var filme = await _db.Filmes.FindAsync(id);
        if (filme is null) return false;
        filme.Assistido = !filme.Assistido;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeletarAsync(int id)
    {
        var filme = await _db.Filmes.FindAsync(id);
        if (filme is null) return false;

        // FK tem ON DELETE CASCADE, mas não depender do PRAGMA foreign_keys da conexão.
        await _db.Progressos.Where(p => p.FilmeId == id).ExecuteDeleteAsync();
        _db.Filmes.Remove(filme);
        await _db.SaveChangesAsync();

        _transcode.LimparCache(id);
        return true;
    }

    /// <summary>
    /// Escaneia a pasta de mídia e adiciona filmes que ainda não estão no banco.
    /// </summary>
    public async Task<int> ScanMediaAsync()
    {
        if (!Directory.Exists(_mediaPath)) return 0;

        var arquivos = Directory.EnumerateFiles(_mediaPath, "*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        var existentes = (await _db.Filmes.AsNoTracking().Select(f => f.ArquivoPath).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var novos = 0;

        foreach (var arquivo in arquivos)
        {
            var relativo = Path.GetRelativePath(_mediaPath, arquivo).Replace('\\', '/');
            if (existentes.Contains(relativo)) continue;

            var titulo = Path.GetFileNameWithoutExtension(arquivo)
                .Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

            _db.Filmes.Add(new Filme { Titulo = titulo, ArquivoPath = relativo });
            novos++;
        }

        if (novos > 0) await _db.SaveChangesAsync();
        return novos;
    }

    /// <summary>
    /// Resolve um ArquivoPath pro caminho absoluto real, recusando qualquer resultado que
    /// escape de _mediaPath (path absoluto injetado, "..", symlink etc.) — ArquivoPath pode
    /// vir de fora (POST /api/filmes), então nunca confiar nele sem checar containment.
    /// </summary>
    public string? ObterCaminhoAbsoluto(string relativePath)
    {
        var raiz = Path.GetFullPath(_mediaPath);
        var full = Path.GetFullPath(Path.Combine(raiz, relativePath));

        var dentroDaRaiz = full == raiz || full.StartsWith(raiz + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!dentroDaRaiz) return null;

        return File.Exists(full) ? full : null;
    }
}
