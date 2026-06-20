using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

public class FilmeService
{
    private readonly AppDbContext _db;
    private readonly string _mediaPath;
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm"];

    public FilmeService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _mediaPath = config.GetValue<string>("MediaPath") ?? "/media";
    }

    public async Task<List<FilmeResponse>> ListarAsync(EGenero? genero = null, bool? assistido = null)
    {
        var query = _db.Filmes.AsNoTracking().AsQueryable();
        if (genero.HasValue) query = query.Where(f => f.Genero == genero.Value);
        if (assistido.HasValue) query = query.Where(f => f.Assistido == assistido.Value);

        return await query.OrderByDescending(f => f.DataAdicionado)
            .Select(f => new FilmeResponse(f.Id, f.Titulo, f.Genero, f.AnoLancamento, f.Diretor, f.ArquivoPath, f.Assistido, f.DataAdicionado))
            .ToListAsync();
    }

    public async Task<FilmeResponse?> ObterAsync(int id)
    {
        var f = await _db.Filmes.FindAsync(id);
        return f is null ? null : new FilmeResponse(f.Id, f.Titulo, f.Genero, f.AnoLancamento, f.Diretor, f.ArquivoPath, f.Assistido, f.DataAdicionado);
    }

    public async Task<FilmeResponse> CriarAsync(FilmeRequest req)
    {
        var filme = new Filme
        {
            Titulo = req.Titulo,
            Genero = req.Genero,
            AnoLancamento = req.AnoLancamento,
            Diretor = req.Diretor,
            ArquivoPath = req.ArquivoPath
        };
        _db.Filmes.Add(filme);
        await _db.SaveChangesAsync();
        return new FilmeResponse(filme.Id, filme.Titulo, filme.Genero, filme.AnoLancamento, filme.Diretor, filme.ArquivoPath, filme.Assistido, filme.DataAdicionado);
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
        _db.Filmes.Remove(filme);
        await _db.SaveChangesAsync();
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

            _db.Filmes.Add(new Filme { Titulo = titulo, ArquivoPath = relativo, Genero = EGenero.Drama });
            novos++;
        }

        if (novos > 0) await _db.SaveChangesAsync();
        return novos;
    }

    public string? ObterCaminhoAbsoluto(string relativePath)
    {
        var full = Path.Combine(_mediaPath, relativePath);
        return File.Exists(full) ? full : null;
    }
}
