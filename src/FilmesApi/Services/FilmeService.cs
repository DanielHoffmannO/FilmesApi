using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

public class FilmeService
{
    private readonly AppDbContext _db;
    private readonly HlsTranscodeService _transcode;
    private readonly ILogger<FilmeService> _logger;
    private readonly string _mediaPath;
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm"];

    // Dois POST /scan concorrentes leriam "não existe" pro mesmo arquivo e ambos inseririam.
    private static readonly SemaphoreSlim _scanLock = new(1, 1);

    public FilmeService(AppDbContext db, HlsTranscodeService transcode, IConfiguration config, ILogger<FilmeService> logger)
    {
        _db = db;
        _transcode = transcode;
        _logger = logger;
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

    /// <summary>Só o ArquivoPath — para o streaming resolver o caminho no disco sem
    /// arrastar o JOIN em Progressos que o <c>ObterAsync</c> completo faz.</summary>
    public Task<string?> ObterArquivoPathAsync(int id)
        => _db.Filmes.AsNoTracking().Where(f => f.Id == id).Select(f => f.ArquivoPath).FirstOrDefaultAsync();

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
    /// Sincroniza o catálogo com a pasta de mídia: importa vídeos novos e remove entradas
    /// cujo arquivo sumiu do disco. Idempotente — rodar de novo não muda nada.
    /// </summary>
    public async Task<ScanResultado> ScanMediaAsync()
    {
        if (!Directory.Exists(_mediaPath)) return new ScanResultado(0, 0);

        await _scanLock.WaitAsync();
        try
        {
            // EnumerarArquivosDeVideo (não Directory.EnumerateFiles direto) porque pula
            // diretórios sem permissão (ex: /media/lost+found) em vez de abortar a varredura
            // inteira no primeiro UnauthorizedAccessException.
            var noDisco = EnumerarArquivosDeVideo(_mediaPath)
                .Select(f => Path.GetRelativePath(_mediaPath, f).Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var noBanco = await _db.Filmes.Where(f => f.ArquivoPath != null).ToListAsync();
            var existentes = noBanco.Select(f => f.ArquivoPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Remove órfãos (arquivo sumiu — ex.: pasta reorganizada). Só quando o disco
            // respondeu com ALGO: se veio vazio, o mount provavelmente caiu — não zerar o catálogo.
            var removidos = 0;
            if (noDisco.Count > 0)
            {
                var orfaos = noBanco.Where(f => !noDisco.Contains(f.ArquivoPath!)).ToList();
                foreach (var orfao in orfaos)
                {
                    await _db.Progressos.Where(p => p.FilmeId == orfao.Id).ExecuteDeleteAsync();
                    _db.Filmes.Remove(orfao);
                    _transcode.LimparCache(orfao.Id);
                    _logger.LogInformation("Scan: removendo órfão {Id} ({Path}) — arquivo não está mais no disco.",
                        orfao.Id, orfao.ArquivoPath);
                }
                removidos = orfaos.Count;
            }

            var novos = 0;
            foreach (var relativo in noDisco)
            {
                if (existentes.Contains(relativo)) continue;
                var titulo = Path.GetFileNameWithoutExtension(relativo)
                    .Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
                _db.Filmes.Add(new Filme { Titulo = titulo, ArquivoPath = relativo });
                novos++;
            }

            if (novos > 0 || removidos > 0) await _db.SaveChangesAsync();
            return new ScanResultado(novos, removidos);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>
    /// Percorre a árvore de mídia recursivamente pulando diretórios que não podem ser lidos
    /// (ex: /media/lost+found, reservado do ext4 e acessível só por root — desde que o
    /// container passou a rodar como usuário não-root, listar esse diretório derruba a
    /// varredura inteira com UnauthorizedAccessException) em vez de deixar propagar.
    /// Directory.EnumerateFiles com SearchOption.AllDirectories não dá pra usar direto aqui
    /// porque ele aborta no primeiro diretório inacessível em vez de pular e continuar.
    /// </summary>
    private static IEnumerable<string> EnumerarArquivosDeVideo(string raiz)
    {
        var pendentes = new Stack<string>();
        pendentes.Push(raiz);

        while (pendentes.Count > 0)
        {
            var dir = pendentes.Pop();
            List<string> subDiretorios;
            List<string> arquivosDoDir;
            try
            {
                subDiretorios = Directory.EnumerateDirectories(dir).ToList();
                arquivosDoDir = Directory.EnumerateFiles(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var sub in subDiretorios) pendentes.Push(sub);
            foreach (var arquivo in arquivosDoDir)
                if (VideoExtensions.Contains(Path.GetExtension(arquivo).ToLowerInvariant()))
                    yield return arquivo;
        }
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
