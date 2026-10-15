using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

public class FilmeService
{
    private readonly AppDbContext _db;
    private readonly HlsTranscodeService _transcode;
    private readonly SubtitleService _legendas;
    private readonly ProgressoService _progresso;
    private readonly ClassificacaoCache _classificacao;
    private readonly ILogger<FilmeService> _logger;
    private readonly string _mediaPath;
    private readonly TimeSpan _estabilidadeScan;
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm"];

    // Dois POST /scan concorrentes leriam "não existe" pro mesmo arquivo e ambos inseririam.
    private static readonly SemaphoreSlim _scanLock = new(1, 1);

    public FilmeService(AppDbContext db, HlsTranscodeService transcode, SubtitleService legendas,
        ProgressoService progresso, ClassificacaoCache classificacao, IConfiguration config, ILogger<FilmeService> logger)
    {
        _db = db;
        _transcode = transcode;
        _legendas = legendas;
        _progresso = progresso;
        _classificacao = classificacao;
        _logger = logger;
        _mediaPath = config.GetValue<string>("MediaPath") ?? "/media";
        // Arquivo novo só entra se o tamanho não mudou entre duas leituras com esta pausa no meio —
        // download/cópia em andamento (sem sufixo .part) não vira filme quebrado no catálogo. 0 desliga.
        _estabilidadeScan = TimeSpan.FromSeconds(config.GetValue<double?>("ScanEstabilidadeSegundos") ?? 2);
    }

    // Projeção SQL — só as colunas do banco + o ponto de retomada (LEFT JOIN em Progressos).
    // Todos os args explícitos: expression tree não aceita construtor com args opcionais.
    // Os campos de classificação vêm depois, em memória (ClassificacaoCache) — regex não roda em SQL.
    private static readonly System.Linq.Expressions.Expression<Func<Filme, FilmeResponse>> ToResponse =
        f => new FilmeResponse(
            f.Id, f.Titulo, f.AnoLancamento, f.ArquivoPath, f.Assistido, f.DataAdicionado,
            f.Progresso != null ? f.Progresso.PosicaoSegundos : (double?)null,
            f.Progresso != null ? f.Progresso.DuracaoSegundos : null,
            f.PosterUrl, f.Sinopse, f.TituloOriginal,
            false, false, null, null, null, null, "", "Sem pasta", null);

    public async Task<List<FilmeResponse>> ListarAsync()
    {
        var lista = await _db.Filmes.AsNoTracking()
            .OrderByDescending(f => f.DataAdicionado).Select(ToResponse).ToListAsync();

        return _classificacao.Classificar(lista);
    }

    /// <summary>Monta a tela pronta pra desenhar — orquestra o que <see cref="CatalogoAgrupador.Montar"/>
    /// (pura) precisa: busca o catálogo inteiro, e só calcula "continuar assistindo" quando
    /// tipo/visto estão neutros (a seção só aparece nesse caso, ver <see cref="CatalogoAgrupador.Montar"/>).
    /// <see cref="ProgressoService.ContinuarAssistindoAsync"/> devolve um resumo
    /// (id/título/posição); aqui cruza por id com o catálogo completo pra ter o
    /// <see cref="FilmeResponse"/> cheio (pôster, classificação etc.) que a tela precisa.</summary>
    public async Task<TelaCatalogoResponse> ObterTelaAsync(string tipo, string visto, string? busca)
    {
        var todos = await ListarAsync();

        var continuarCandidatos = new List<FilmeResponse>();
        if (tipo == "all" && visto == "all")
        {
            var continuarBase = await _progresso.ContinuarAssistindoAsync();
            var porId = todos.ToDictionary(f => f.Id);
            continuarCandidatos = continuarBase
                .Select(c => porId.GetValueOrDefault(c.Id))
                .OfType<FilmeResponse>()
                .ToList();
        }

        return CatalogoAgrupador.Montar(todos, continuarCandidatos, tipo, visto, busca);
    }

    /// <summary>Episódios faltando, repetidos, não reconhecidos e propaganda no catálogo inteiro
    /// (ver <see cref="DiagnosticoCatalogo"/>). Os tamanhos vêm do disco, só pros arquivos que precisam.</summary>
    public async Task<DiagnosticoResponse> DiagnosticoAsync()
    {
        var catalogo = await ListarAsync();
        return DiagnosticoCatalogo.Analisar(catalogo, relativo =>
        {
            var absoluto = ObterCaminhoAbsoluto(relativo);
            try { return absoluto is null ? null : new FileInfo(absoluto).Length; }
            catch (IOException) { return null; }
        });
    }

    /// <summary>Via <see cref="ListarAsync"/> (não uma query própria) de propósito — classificar
    /// só este filme sem saber quantos irmãos numerados a pasta tem daria um resultado diferente
    /// do que a lista mostra pra pasta tipo antologia sem marcador de temporada (ver
    /// <see cref="MediaNomeParser.ContarNumeradosPorPasta"/>).</summary>
    public async Task<FilmeResponse?> ObterAsync(int id) =>
        (await ListarAsync()).FirstOrDefault(f => f.Id == id);

    public Task<bool> ExisteAsync(int id) => _db.Filmes.AnyAsync(f => f.Id == id);

    /// <summary>Próximo episódio da mesma série na ordem (temporada, episódio). null quando
    /// não há: id não existe, não é episódio, é o último, ou é um "extra".</summary>
    public async Task<FilmeResponse?> ProximoEpisodioAsync(int id)
    {
        var todos = await ListarAsync();
        var atual = todos.FirstOrDefault(f => f.Id == id);
        if (atual is null || !atual.EhEpisodio || atual.EhExtra) return null;

        var episodios = todos
            .Where(f => f.EhEpisodio && !f.EhExtra && f.SerieChave == atual.SerieChave)
            .OrderBy(f => f.Temporada ?? 0)
            .ThenBy(f => f.Episodio ?? 0)
            .ThenBy(f => f.Titulo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var i = episodios.FindIndex(f => f.Id == id);
        return i >= 0 && i + 1 < episodios.Count ? episodios[i + 1] : null;
    }

    /// <summary>Caminho absoluto do arquivo no disco, ou null se o filme não existe ou o
    /// arquivo sumiu. Lê só o ArquivoPath — sem o JOIN em Progressos do <c>ObterAsync</c>.</summary>
    public async Task<string?> ResolverCaminhoAsync(int id)
    {
        var arquivoPath = await _db.Filmes.AsNoTracking()
            .Where(f => f.Id == id).Select(f => f.ArquivoPath).FirstOrDefaultAsync();
        return arquivoPath is null ? null : ObterCaminhoAbsoluto(arquivoPath);
    }

    public async Task<bool> MarcarAssistidoAsync(int id)
    {
        var filme = await _db.Filmes.FindAsync(id);
        if (filme is null) return false;
        filme.Assistido = !filme.Assistido;
        await _db.SaveChangesAsync();
        if (filme.Assistido) _transcode.DescartarCaches(id);   // já visto: não deixa GB de cache gravados à toa
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

            // Remove órfãos (arquivo sumiu — ex.: pasta reorganizada, ou apagada de propósito), exceto quando
            // o sumiço parece montagem caída (ver PrefixosRemoviveis) — apagaria o catálogo e o progresso de
            // assistido do disco que caiu.
            var prefixosVivos = noDisco.Select(PrefixoDe).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prefixosNoBanco = noBanco.Select(f => PrefixoDe(f.ArquivoPath!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var removiveis = PrefixosRemoviveis(prefixosNoBanco, prefixosVivos, noDisco.Count, PastaVaziaOuInacessivel);
            var candidatos = noBanco.Where(f => !noDisco.Contains(f.ArquivoPath!)).ToList();
            foreach (var prefixo in candidatos.Select(f => PrefixoDe(f.ArquivoPath!))
                         .Where(pr => !removiveis.Contains(pr)).Distinct(StringComparer.OrdinalIgnoreCase))
                _logger.LogWarning(
                    "Scan: nenhum arquivo em '{Prefixo}/' no disco — montagem caída? Itens desse prefixo mantidos no catálogo.",
                    prefixo.Length == 0 ? "(raiz)" : prefixo);

            var orfaos = candidatos.Where(f => removiveis.Contains(PrefixoDe(f.ArquivoPath!))).ToList();
            foreach (var orfao in orfaos)
            {
                await _db.Progressos.Where(p => p.FilmeId == orfao.Id).ExecuteDeleteAsync();
                _db.Filmes.Remove(orfao);
                _transcode.LimparCache(orfao.Id);
                _legendas.LimparCache(orfao.Id);
                _logger.LogInformation("Scan: removendo órfão {Id} ({Path}) — arquivo não está mais no disco.",
                    orfao.Id, orfao.ArquivoPath);
            }
            var removidos = orfaos.Count;

            var novos = 0;
            var aImportar = await SoArquivosEstaveisAsync(noDisco.Where(r => !existentes.Contains(r)).ToList());
            foreach (var relativo in aImportar)
            {
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

    /// <summary>Pasta de 1º nível do caminho relativo (<c>extra/Séries/x.mkv</c> → <c>extra</c>); vazio
    /// pra arquivo direto na raiz. Cada uma pode ser um disco/montagem diferente.</summary>
    /// <summary>Quais prefixos (pasta de 1º nível; "" = raiz) podem ter os órfãos REMOVIDOS do catálogo.
    /// Prefixo com arquivo no disco: sempre. Prefixo sem NENHUM arquivo no disco é ambíguo — pasta apagada
    /// de propósito ou disco que caiu — e só é liberado quando nenhum sinal de queda aparece:
    /// (1) disco todo vazio, nada é removido; (2) a raiz sem arquivo fica protegida; (3) a pasta ainda
    /// existe mas está vazia = ponto de montagem sem disco (o /media/extra com o HD de backup fora);
    /// (4) metade ou mais dos prefixos sumiu junto = disco principal caído, não uma limpeza.
    /// Sobra o caso comum: UMA pasta inteira apagada (o filme/temporada que o dono removeu) — esse
    /// vai embora do catálogo, senão fica um fantasma que dá 404 pra sempre.</summary>
    internal static HashSet<string> PrefixosRemoviveis(
        IReadOnlyCollection<string> prefixosNoBanco, IReadOnlySet<string> prefixosVivos,
        int arquivosNoDisco, Func<string, bool> pastaVaziaOuInacessivel)
    {
        var removiveis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (arquivosNoDisco == 0) return removiveis;

        var mortos = prefixosNoBanco.Count(p => !prefixosVivos.Contains(p));
        var emMassa = prefixosNoBanco.Count > 0 && mortos * 2 >= prefixosNoBanco.Count;
        foreach (var p in prefixosNoBanco)
        {
            if (prefixosVivos.Contains(p)) removiveis.Add(p);
            else if (p.Length > 0 && !emMassa && !pastaVaziaOuInacessivel(p)) removiveis.Add(p);
        }
        return removiveis;
    }

    private bool PastaVaziaOuInacessivel(string prefixo)
    {
        try
        {
            var dir = Path.Combine(_mediaPath, prefixo);
            return Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static string PrefixoDe(string relativo)
    {
        var barra = relativo.IndexOf('/');
        return barra < 0 ? "" : relativo[..barra];
    }

    /// <summary>Tira os arquivos cujo tamanho mudou durante a pausa de <c>ScanEstabilidadeSegundos</c>
    /// (ainda sendo baixados/copiados). Eles entram no próximo scan, já completos.</summary>
    private async Task<List<string>> SoArquivosEstaveisAsync(List<string> candidatos)
    {
        if (candidatos.Count == 0 || _estabilidadeScan <= TimeSpan.Zero) return candidatos;

        long Tamanho(string relativo)
        {
            try { return new FileInfo(Path.Combine(_mediaPath, relativo)).Length; }
            catch (IOException) { return -1; }
        }

        var antes = candidatos.ToDictionary(c => c, Tamanho);
        await Task.Delay(_estabilidadeScan);

        var estaveis = new List<string>(candidatos.Count);
        foreach (var c in candidatos)
        {
            var depois = Tamanho(c);
            if (depois >= 0 && depois == antes[c]) estaveis.Add(c);
            else _logger.LogInformation("Scan: '{Arquivo}' ainda está sendo escrito (ou sumiu) — fica pro próximo scan.", c);
        }
        return estaveis;
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
    private string? ObterCaminhoAbsoluto(string relativePath)
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
