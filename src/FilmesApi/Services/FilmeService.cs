using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

public class FilmeService
{
    private readonly AppDbContext _db;
    private readonly HlsTranscodeService _transcode;
    private readonly SubtitleService _legendas;
    private readonly ILogger<FilmeService> _logger;
    private readonly string _mediaPath;
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm"];

    // Dois POST /scan concorrentes leriam "não existe" pro mesmo arquivo e ambos inseririam.
    private static readonly SemaphoreSlim _scanLock = new(1, 1);

    public FilmeService(AppDbContext db, HlsTranscodeService transcode, SubtitleService legendas, IConfiguration config, ILogger<FilmeService> logger)
    {
        _db = db;
        _transcode = transcode;
        _legendas = legendas;
        _logger = logger;
        _mediaPath = config.GetValue<string>("MediaPath") ?? "/media";
    }

    // Projeção SQL — só as colunas do banco + o ponto de retomada (LEFT JOIN em Progressos).
    // Todos os args explícitos: expression tree não aceita construtor com args opcionais.
    // Os campos de classificação vêm depois, em memória (ComClassificacao) — regex não roda em SQL.
    private static readonly System.Linq.Expressions.Expression<Func<Filme, FilmeResponse>> ToResponse =
        f => new FilmeResponse(
            f.Id, f.Titulo, f.AnoLancamento, f.ArquivoPath, f.Assistido, f.DataAdicionado,
            f.Progresso != null ? f.Progresso.PosicaoSegundos : (double?)null,
            f.Progresso != null ? f.Progresso.DuracaoSegundos : null,
            f.PosterUrl, f.Sinopse, f.TituloOriginal,
            false, false, null, null, null, null, "", "Sem pasta");

    /// <summary>Preenche série/episódio/rótulo a partir do caminho do arquivo.</summary>
    public static FilmeResponse ComClassificacao(FilmeResponse f)
    {
        var c = MediaNomeParser.Classificar(f.ArquivoPath, f.Titulo);
        return f with
        {
            EhEpisodio = c.EhEpisodio,
            EhExtra = c.EhExtra,
            Serie = c.Serie,
            SerieChave = c.SerieChave,
            Temporada = c.Temporada,
            Episodio = c.Episodio,
            Rotulo = c.Rotulo,
            Pasta = c.Pasta,
        };
    }

    public async Task<List<FilmeResponse>> ListarAsync(bool? assistido = null)
    {
        var query = _db.Filmes.AsNoTracking().AsQueryable();
        if (assistido.HasValue) query = query.Where(f => f.Assistido == assistido.Value);

        var lista = await query.OrderByDescending(f => f.DataAdicionado).Select(ToResponse).ToListAsync();
        return lista.Select(ComClassificacao).ToList();
    }

    /// <summary>Filtra (tipo/visto/busca) e agrupa (filme solto / pasta filme+extras / série)
    /// pra tela desenhar direto — função pura, testável sem banco. <c>continuarCandidatos</c>
    /// já vem enriquecido e na ordem certa (mais recente primeiro); esta função só decide se
    /// a seção aparece (tipo e visto neutros) e aplica a busca nela também.</summary>
    public static TelaCatalogoResponse MontarTela(
        List<FilmeResponse> todos, List<FilmeResponse> continuarCandidatos,
        string tipo, string visto, string? busca)
    {
        var q = string.IsNullOrWhiteSpace(busca) ? null : busca.Trim().ToLowerInvariant();
        bool PassaBusca(FilmeResponse f) =>
            q is null || (f.Titulo + " " + (f.Serie ?? "") + " " + (f.Pasta ?? "")).ToLowerInvariant().Contains(q);

        var continuarAssistindo = tipo == "all" && visto == "all"
            ? continuarCandidatos.Where(PassaBusca).ToList()
            : [];

        bool PassaFiltro(FilmeResponse f) =>
            (visto != "assistido" || f.Assistido) &&
            (visto != "nao-assistido" || !f.Assistido) &&
            (tipo != "filme" || !f.EhEpisodio) &&
            (tipo != "serie" || f.EhEpisodio) &&
            PassaBusca(f);

        // Contagem por pasta na lista INTEIRA (não na filtrada) — senão a busca "achataria"
        // uma pasta de filme+extras só por esconder temporariamente os outros arquivos dela.
        var porPasta = todos.GroupBy(f => f.Pasta).ToDictionary(g => g.Key, g => g.Count());

        var filmesSoltos = new List<FilmeResponse>();
        var pastasFilme = new Dictionary<string, List<FilmeResponse>>();
        // Nomes: conta ocorrência de cada Serie "bruto" dentro do grupo -- decide o nome de
        // exibição pelo MAIS FREQUENTE (não o primeiro visto), senão um apelido/pasta de
        // release feia com poucos episódios podia "vencer" a exibição de um grupo com
        // centenas de episódios só por coincidência de ordem (ver ApelidosSerie).
        var series = new Dictionary<string, (Dictionary<string, int> Nomes, List<FilmeResponse> Itens)>();

        foreach (var f in todos.Where(PassaFiltro))
        {
            if (f.EhEpisodio)
            {
                var chave = f.SerieChave ?? f.Serie ?? "";
                if (!series.TryGetValue(chave, out var g)) { g = ([], []); series[chave] = g; }
                var nome = f.Serie ?? "";
                g.Nomes[nome] = g.Nomes.GetValueOrDefault(nome) + 1;
                g.Itens.Add(f);
                continue;
            }
            if (f.Pasta != "Sem pasta" && porPasta.GetValueOrDefault(f.Pasta) > 1)
            {
                if (!pastasFilme.TryGetValue(f.Pasta, out var lista)) { lista = []; pastasFilme[f.Pasta] = lista; }
                lista.Add(f);
            }
            else filmesSoltos.Add(f);
        }

        // pasta com 1 filme "de verdade" + só extras (trailer/sample) -> mostra o filme direto
        foreach (var pasta in pastasFilme.Keys.ToList())
        {
            var principais = pastasFilme[pasta].Where(f => !f.EhExtra).ToList();
            if (principais.Count == 1)
            {
                filmesSoltos.Add(principais[0]);
                pastasFilme.Remove(pasta);
            }
        }

        filmesSoltos.Sort((a, b) => string.Compare(a.Titulo, b.Titulo, StringComparison.OrdinalIgnoreCase));

        foreach (var lista in series.Values.Select(v => v.Itens))
            lista.Sort((a, b) =>
            {
                var c = (a.Temporada ?? 0).CompareTo(b.Temporada ?? 0);
                if (c != 0) return c;
                c = (a.Episodio ?? 0).CompareTo(b.Episodio ?? 0);
                return c != 0 ? c : string.Compare(a.Titulo, b.Titulo, StringComparison.OrdinalIgnoreCase);
            });

        var pastasOrdenadas = pastasFilme
            .Select(kv => new PastaAgrupada(kv.Key, MediaNomeParser.NomePastaExibicao(kv.Key), kv.Value))
            .OrderBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Nome de exibição = o mais frequente no grupo; empate resolvido alfabeticamente,
        // só pra dar um resultado determinístico (não devia acontecer na prática).
        string NomeMaisFrequente(Dictionary<string, int> nomes) => nomes
            .OrderByDescending(nc => nc.Value)
            .ThenBy(nc => nc.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;

        var seriesOrdenadas = series
            .Select(kv => new SerieAgrupada(kv.Key, NomeMaisFrequente(kv.Value.Nomes), kv.Value.Itens))
            .OrderBy(s => s.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TelaCatalogoResponse(continuarAssistindo, filmesSoltos, pastasOrdenadas, seriesOrdenadas, todos.Count == 0);
    }

    public async Task<FilmeResponse?> ObterAsync(int id)
    {
        var f = await _db.Filmes.AsNoTracking()
            .Where(f => f.Id == id)
            .Select(ToResponse)
            .FirstOrDefaultAsync();
        return f is null ? null : ComClassificacao(f);
    }

    public Task<bool> ExisteAsync(int id) => _db.Filmes.AnyAsync(f => f.Id == id);

    /// <summary>Próximo episódio da mesma série na ordem (temporada, episódio). null quando
    /// não há: id não existe, não é episódio, é o último, ou é um "extra".</summary>
    public async Task<FilmeResponse?> ProximoEpisodioAsync(int id)
    {
        var atual = await ObterAsync(id);
        if (atual is null || !atual.EhEpisodio || atual.EhExtra) return null;

        var episodios = (await ListarAsync())
            .Where(f => f.EhEpisodio && !f.EhExtra && f.SerieChave == atual.SerieChave)
            .OrderBy(f => f.Temporada ?? 0)
            .ThenBy(f => f.Episodio ?? 0)
            .ThenBy(f => f.Titulo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var i = episodios.FindIndex(f => f.Id == id);
        return i >= 0 && i + 1 < episodios.Count ? episodios[i + 1] : null;
    }

    /// <summary>Só o ArquivoPath — para o streaming resolver o caminho no disco sem
    /// arrastar o JOIN em Progressos que o <c>ObterAsync</c> completo faz.</summary>
    public Task<string?> ObterArquivoPathAsync(int id)
        => _db.Filmes.AsNoTracking().Where(f => f.Id == id).Select(f => f.ArquivoPath).FirstOrDefaultAsync();

    public async Task<bool> MarcarAssistidoAsync(int id)
    {
        var filme = await _db.Filmes.FindAsync(id);
        if (filme is null) return false;
        filme.Assistido = !filme.Assistido;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Nome "cru" do arquivo (só troca <c>. _ -</c> por espaço). É o que o título
    /// vale antes de qualquer limpeza — serve de marcador "ninguém mexeu nisso".</summary>
    private static string TituloCru(string relativoPath) =>
        Path.GetFileNameWithoutExtension(relativoPath).Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

    /// <summary>Título + ano só a partir do nome do arquivo, sem lixo de release
    /// (<c>720p</c>, <c>x264</c>, <c>DUAL</c>, <c>SxxExx</c>…). Mesmo caminho do
    /// <see cref="MediaNomeParser.TituloParaBusca(string?, string?)"/> — tela e busca no TMDB batem.</summary>
    private static (string Titulo, int? Ano) DeduzirTitulo(string relativoPath)
    {
        var cru = TituloCru(relativoPath);
        var (limpo, ano) = MediaNomeParser.TituloParaBusca(relativoPath, cru);
        return (string.IsNullOrWhiteSpace(limpo) ? cru : limpo, ano);
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
                    _legendas.LimparCache(orfao.Id);
                    _logger.LogInformation("Scan: removendo órfão {Id} ({Path}) — arquivo não está mais no disco.",
                        orfao.Id, orfao.ArquivoPath);
                }
                removidos = orfaos.Count;
            }

            var novos = 0;
            foreach (var relativo in noDisco)
            {
                if (existentes.Contains(relativo)) continue;
                var (titulo, ano) = DeduzirTitulo(relativo);
                _db.Filmes.Add(new Filme { Titulo = titulo, AnoLancamento = ano, ArquivoPath = relativo });
                novos++;
            }

            // Re-limpa título de linhas antigas que ainda estão com o nome CRU do arquivo
            // (ninguém renomeou, o TMDB não tocou) — pra "Breaking.Bad.2011.S04E09.720p.x264"
            // virar "Breaking Bad". Roda de novo não muda mais nada (título já não é o cru).
            var limpos = 0;
            foreach (var filme in noBanco)
            {
                if (filme.ArquivoPath is null || !noDisco.Contains(filme.ArquivoPath)) continue;
                if (filme.Titulo != TituloCru(filme.ArquivoPath)) continue;
                var (titulo, ano) = DeduzirTitulo(filme.ArquivoPath);
                if (titulo == filme.Titulo) continue;
                filme.Titulo = titulo;
                filme.AnoLancamento ??= ano;
                limpos++;
            }

            if (novos > 0 || removidos > 0 || limpos > 0) await _db.SaveChangesAsync();
            return new ScanResultado(novos, removidos, limpos);
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
    internal static IEnumerable<string> EnumerarArquivosDeVideo(string raiz)
    {
        var pendentes = new Stack<string>();
        pendentes.Push(raiz);
        // Symlink apontando pra um ancestral (ou pra si mesmo) faria o Stack crescer pra
        // sempre — visitados guarda o caminho REAL (resolvendo o link) de cada pasta já
        // processada, então um ciclo bate aqui e para, em vez de escanear infinitamente.
        var visitados = new HashSet<string>(StringComparer.Ordinal);

        while (pendentes.Count > 0)
        {
            var dir = pendentes.Pop();
            if (!visitados.Add(CaminhoReal(dir))) continue;

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

    // Segue a cadeia de symlink até o alvo final (ou o próprio caminho absoluto, se não for
    // link nenhum) — é essa forma resolvida que serve pra detectar ciclo, não o caminho como
    // foi alcançado (dois links diferentes pro mesmo lugar real também não escaneiam 2x).
    private static string CaminhoReal(string dir)
    {
        var full = Path.GetFullPath(dir);
        try { return Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>
    /// Resolve um ArquivoPath pro caminho absoluto real, recusando qualquer resultado que
    /// escape de _mediaPath (path absoluto injetado, "..", symlink etc.). Hoje ArquivoPath só
    /// entra no banco pelo scan (ScanMediaAsync, sempre a partir de arquivo real em disco), mas
    /// a checagem de containment fica como defesa em profundidade — barata e nunca confiar
    /// cegamente num caminho vindo do banco pra montar um caminho de arquivo.
    /// </summary>
    public string? ObterCaminhoAbsoluto(string relativePath)
    {
        var raiz = Path.GetFullPath(_mediaPath);
        var full = Path.GetFullPath(Path.Combine(raiz, relativePath));

        var dentroDaRaiz = full == raiz || full.StartsWith(raiz + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!dentroDaRaiz) return null;
        if (!File.Exists(full)) return null;

        // Path.GetFullPath só normaliza a string — não segue symlink. Um link dentro de
        // _mediaPath apontando pra fora (ex.: /media/x.mp4 -> /etc/shadow) passaria no
        // containment acima. Resolve o alvo real e re-checa.
        var alvoReal = TentarResolverLink(full);
        return alvoReal.StartsWith(raiz + Path.DirectorySeparatorChar, StringComparison.Ordinal) || alvoReal == raiz
            ? full : null;
    }

    private static string TentarResolverLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var alvo = info.ResolveLinkTarget(returnFinalTarget: true);
            return alvo is null ? path : Path.GetFullPath(alvo.FullName);
        }
        catch (IOException) { return path; }
    }
}
