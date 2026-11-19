using System.Text.Json;
using FilmesApi.Data;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// Enriquece em background os filmes que ainda não têm metadados (<c>MetadadosEm == null</c>):
/// busca no TMDB pelo nome do arquivo e grava título oficial, sinopse e URL do pôster.
/// Não faz nada se o <see cref="TmdbService"/> estiver desligado (sem <c>TmdbApiKey</c>).
/// </summary>
public class MetadataService : BackgroundService
{
    private const int TamanhoLote = 20;

    private readonly IServiceScopeFactory _escopos;
    private readonly TmdbService _tmdb;
    private readonly ILogger<MetadataService> _logger;
    private readonly RegistroDeTarefas? _tarefas;
    private readonly NomesPersonalizadosService? _nomes;
    private readonly SemaphoreSlim _acordar = new(0, 1);
    private readonly HashSet<string> _overridesAplicados = [];   // "chave|id" já invalidados neste processo (evita laço se o id der 404)
    // Um match por SÉRIE (não por episódio), guardado entre lotes: 35 capítulos de uma novela = 1 busca. Erro também fica (1 h) pra não repetir a cada lote.
    private readonly Dictionary<string, (TmdbResultado? Res, DateTime Quando)> _cacheSeries = [];
    private static readonly TimeSpan ValidadeDoErroDeSerie = TimeSpan.FromHours(1);
    private readonly HashSet<string> _semMatchLogado = [];   // "tipo|termo" já avisado neste processo
    private bool _varreuSemMatch;   // a varredura de episódios sem match já rodou neste processo
    private bool _overridesSujos = true;   // há override novo (ou é o 1º lote) pra conferir; só o nomes.json mexendo religa   // "chave|id" já invalidados neste processo (evita laço se o id der 404)

    public MetadataService(IServiceScopeFactory escopos, TmdbService tmdb, ILogger<MetadataService> logger,
        RegistroDeTarefas? tarefas = null, NomesPersonalizadosService? nomes = null)
    {
        _nomes = nomes;
        if (nomes is not null)
            nomes.TmdbMudou += () => { _overridesSujos = true; try { _acordar.Release(); } catch (SemaphoreFullException) { /* já acordado */ } };
        _tarefas = tarefas;
        _escopos = escopos;
        _tmdb = tmdb;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_tmdb.Habilitado) { _tarefas?.Declarar("metadados", ligada: false); return; }
        _tarefas?.Declarar("metadados");

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan pausa;
            var medicao = _tarefas?.Iniciar("metadados");
            try
            {
                var processados = await ProcessarLoteAsync(stoppingToken);
                medicao?.Ok(processados > 0 ? $"{processados} filme(s) enriquecido(s)" : "nada pendente");
                // Processou algo → segue logo pro próximo lote. Nada pendente → dorme bastante
                // (novos arquivos só aparecem no próximo scan).
                pausa = processados > 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromHours(6);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Falha de rede/DB/schema — tenta de novo em 10 min, não daqui a 6h.
                medicao?.Falhou(ex);
                _logger.LogWarning(ex, "Enriquecimento de metadados: lote falhou, nova tentativa em 10 min.");
                pausa = TimeSpan.FromMinutes(10);
            }

            try { await _acordar.WaitAsync(pausa, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Chave de override de um item: título de busca normalizado (e, p/ episódio, também o nome da série).</summary>
    internal static IEnumerable<string> ChavesDeOverride(string? arquivoPath, string? tituloFallback, IReadOnlyDictionary<string, int>? numerados = null)
    {
        var (titulo, _) = MediaNomeParser.TituloParaBusca(arquivoPath, tituloFallback);
        if (!string.IsNullOrWhiteSpace(titulo)) yield return MediaNomeParser.NormalizarBusca(titulo);
        if (MediaNomeParser.EhEpisodio(arquivoPath, numerados))
        {
            var s = MediaNomeParser.ChaveSerie(arquivoPath);
            if (!string.IsNullOrWhiteSpace(s)) yield return MediaNomeParser.NormalizarBusca(s);
            // e o nome de exibição (o que o diagnóstico mostra como "buscado": "Hora de Aventura", "Diários de Um Vampiro")
            var exibicao = MediaNomeParser.NomeDaSerieParaBusca(arquivoPath);
            if (!string.IsNullOrWhiteSpace(exibicao)) yield return MediaNomeParser.NormalizarBusca(exibicao);
        }
    }

    /// <summary>Override vigente do item, se houver.</summary>
    internal static int? OverrideDe(IReadOnlyDictionary<string, int> overrides, string? arquivoPath, string? tituloFallback, IReadOnlyDictionary<string, int>? numerados = null)
    {
        if (overrides.Count == 0) return null;
        foreach (var k in ChavesDeOverride(arquivoPath, tituloFallback, numerados))
            if (overrides.TryGetValue(k, out var id)) return id;
        return null;
    }

    /// <summary>Re-enriquece só os itens cujo override de tmdbId é novo (ou mudou): zera o <c>MetadadosEm</c> deles.</summary>
    private async Task InvalidarOverridesNovosAsync(AppDbContext db, IReadOnlyDictionary<string, int> numerados, CancellationToken ct)
    {
        var overrides = _nomes?.TmdbOverrides;
        if (!_overridesSujos) return;   // nenhum override novo desde a última conferência: não relê o catálogo inteiro a cada lote
        _overridesSujos = false;
        if (overrides is null || overrides.Count == 0) return;

        var candidatos = await db.Filmes.Where(f => f.ArquivoPath != null && f.MetadadosEm != null).ToListAsync(ct);
        var invalidados = 0;
        foreach (var f in candidatos)
        {
            if (OverrideDe(overrides, f.ArquivoPath, f.Titulo, numerados) is not int id || f.TmdbId == id) continue;
            if (!_overridesAplicados.Add($"{f.Id}|{id}")) continue;
            f.MetadadosEm = null;
            invalidados++;
        }
        if (invalidados > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Metadados: {N} item(ns) voltam pro enriquecimento por override de tmdbId.", invalidados);
        }
    }

    private bool _varreuNaoLatinos;

    /// <summary>Uma vez por execução: filme já enriquecido cujo título ficou em japonês/coreano/etc. (de antes de o serviço buscar a
    /// tradução em português) volta pro enriquecimento, que agora troca pelo título em português.</summary>
    private async Task ReenriquecerTitulosNaoLatinosAsync(AppDbContext db, IReadOnlyDictionary<string, int> numerados, CancellationToken ct)
    {
        if (_varreuNaoLatinos || !_tmdb.Habilitado) return;
        _varreuNaoLatinos = true;

        var candidatos = await db.Filmes.Where(f => f.ArquivoPath != null && f.MetadadosEm != null).ToListAsync(ct);
        var voltam = candidatos.Where(f => TmdbService.TemEscritaNaoLatina(f.Titulo) && !MediaNomeParser.EhEpisodio(f.ArquivoPath, numerados)).ToList();
        if (voltam.Count == 0) return;
        foreach (var f in voltam) f.MetadadosEm = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Metadados: {N} título(s) em escrita não latina voltam pro enriquecimento (título em português).", voltam.Count);
    }

    private bool _varreuSeries;

    /// <summary>Uma vez por execução: episódio já enriquecido antes de o serviço guardar o nome da série no TMDB volta pro enriquecimento.</summary>
    private async Task ReenriquecerSeriesSemNomeAsync(AppDbContext db, IReadOnlyDictionary<string, int> numerados, CancellationToken ct)
    {
        if (_varreuSeries || !_tmdb.Habilitado) return;
        _varreuSeries = true;

        var candidatos = await db.Filmes.Where(f => f.ArquivoPath != null && f.MetadadosEm != null && f.SerieTitulo == null).ToListAsync(ct);
        var voltam = candidatos.Where(f => MediaNomeParser.EhEpisodio(f.ArquivoPath, numerados)).ToList();
        if (voltam.Count == 0) return;
        foreach (var f in voltam) f.MetadadosEm = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Metadados: {N} episódio(s) voltam pro enriquecimento pra guardar o nome da série no TMDB.", voltam.Count);
    }

    /// <summary>Uma vez por execução: episódio que passou pelo enriquecimento SEM match (ex.: novela buscada pelo nome do arquivo) volta pra fila — agora
    /// o match é por série. Filme sem match não volta (nada mudou pra ele).</summary>
    private async Task ReenriquecerEpisodiosSemMatchAsync(AppDbContext db, IReadOnlyDictionary<string, int> numerados, CancellationToken ct)
    {
        if (_varreuSemMatch || !_tmdb.Habilitado) return;
        _varreuSemMatch = true;

        var candidatos = await db.Filmes.Where(f => f.ArquivoPath != null && f.MetadadosEm != null && f.TmdbId == null).ToListAsync(ct);
        var voltam = candidatos.Where(f => MediaNomeParser.EhEpisodio(f.ArquivoPath, numerados)).ToList();
        if (voltam.Count == 0) return;
        foreach (var f in voltam) f.MetadadosEm = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Metadados: {N} episódio(s) sem match voltam pro enriquecimento (agora por série).", voltam.Count);
    }

    /// <summary>Busca no TMDB: série primeiro pelo NOME DA SÉRIE (pasta) — um resultado vale pra todos os episódios — e só se não achar tenta
    /// o título tirado do arquivo; filme pelo título. Registra o motivo quando não casa. Lança em erro de transporte (o lote para).</summary>
    private async Task<TmdbResultado?> ResolverAsync(Models.Filme filme, string titulo, int? ano, bool serie, int? overrideId, CancellationToken ct)
    {
        TmdbResultado? res = null;
        if (overrideId is int idForcado)
        {
            // id inexistente (404) devolve null: cai na busca normal em vez de deixar o item sem metadados
            res = await _tmdb.BuscarPorIdAsync(idForcado, serie, ct);
            await PausaTmdb(ct);
        }
        if (res is not null) return res;

        if (serie)
        {
            // pelo NOME DA SÉRIE: primeiro o de exibição (apelidos: "Hora da Aventura" → "Hora de Aventura", "DDUV" → "Diários de Um Vampiro"), depois o limpo da pasta
            var cru = MediaNomeParser.ChaveSerie(filme.ArquivoPath);
            var exibicao = MediaNomeParser.NomeDaSerieParaBusca(filme.ArquivoPath);
            foreach (var nome in new[] { exibicao, cru }.Where(n => !string.IsNullOrWhiteSpace(n)).DistinctBy(MediaNomeParser.NormalizarBusca))
            {
                res = await BuscarSerieEmCacheAsync(nome, ano, ct);
                if (res is not null) return res;
            }
            AvisarSemMatch("tv", exibicao, titulo);
            if (string.IsNullOrWhiteSpace(titulo) || MediaNomeParser.NormalizarBusca(titulo) == MediaNomeParser.NormalizarBusca(exibicao)) return null;
        }
        if (string.IsNullOrWhiteSpace(titulo)) return null;
        // o título do arquivo também fica em cache (série cujo nome da pasta não casou: não repete a busca por episódio de mesmo título)
        var chaveTitulo = $"{(serie ? "tvt" : "mv")}|{MediaNomeParser.NormalizarBusca(titulo)}|{ano}";
        if (_cacheSeries.TryGetValue(chaveTitulo, out var g2) && (g2.Res is not null || DateTime.UtcNow - g2.Quando < ValidadeDoErroDeSerie)) return g2.Res;
        res = await _tmdb.BuscarAsync(titulo, ano, serie, ct);
        await PausaTmdb(ct);
        _cacheSeries[chaveTitulo] = (res, DateTime.UtcNow);
        // filme que não casou pelo nome do arquivo: tenta o nome da PASTA ("Guerra de Canudos (1997)/Guerra dos Canudos…mp4")
        if (res is null && !serie && MediaNomeParser.TituloDaPastaParaBusca(filme.ArquivoPath) is var (tituloPasta, anoPasta)
            && MediaNomeParser.NormalizarBusca(tituloPasta) != MediaNomeParser.NormalizarBusca(titulo))
        {
            res = await _tmdb.BuscarAsync(tituloPasta, anoPasta ?? ano, serie: false, ct);
            await PausaTmdb(ct);
            _cacheSeries[chaveTitulo] = (res, DateTime.UtcNow);
        }
        if (res is null) AvisarSemMatch(serie ? "tv" : "movie", titulo, null);
        return res;
    }

    /// <summary>Uma busca de série por nome, lembrada entre lotes (acerto fica; erro, 1 h).</summary>
    private async Task<TmdbResultado?> BuscarSerieEmCacheAsync(string nome, int? ano, CancellationToken ct)
    {
        var chave = $"tv|{MediaNomeParser.NormalizarBusca(nome)}|{ano}";
        if (_cacheSeries.TryGetValue(chave, out var guardado) && (guardado.Res is not null || DateTime.UtcNow - guardado.Quando < ValidadeDoErroDeSerie))
            return guardado.Res;
        var res = await _tmdb.BuscarAsync(nome, ano, serie: true, ct);
        await PausaTmdb(ct);
        _cacheSeries[chave] = (res, DateTime.UtcNow);
        return res;
    }

    /// <summary>Intervalo de cortesia depois de CADA chamada ao TMDB (resposta vinda do cache de série não espera nada).</summary>
    private static Task PausaTmdb(CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(300), ct);

    private void AvisarSemMatch(string tipo, string buscado, string? alternativa)
    {
        if (!_semMatchLogado.Add($"{tipo}|{buscado}|{alternativa}")) return;
        _logger.LogInformation("Metadados: sem match no TMDB (/search/{Tipo}) para '{Buscado}'{Alt} — use o campo tmdbId do diagnóstico se souber o id.",
            tipo, buscado, alternativa is { Length: > 0 } ? $" (e pelo título do arquivo '{alternativa}')" : "");
    }

    internal async Task<int> ProcessarLoteAsync(CancellationToken ct)
    {
        using var escopo = _escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        // a mesma conta da grade: pasta de antologia sem SxxExx ("13- Pingu e a gaivota", "36 Fusao Fria") conta como série aqui também
        var numerados = MediaNomeParser.ContarNumeradosPorPasta(await db.Filmes.Select(f => f.ArquivoPath).ToListAsync(ct));
        await InvalidarOverridesNovosAsync(db, numerados, ct);
        await ReenriquecerTitulosNaoLatinosAsync(db, numerados, ct);
        await ReenriquecerSeriesSemNomeAsync(db, numerados, ct);
        await ReenriquecerEpisodiosSemMatchAsync(db, numerados, ct);

        var pendentes = await db.Filmes
            .Where(f => f.MetadadosEm == null && f.ArquivoPath != null)
            .OrderBy(f => f.Id)
            .Take(TamanhoLote)
            .ToListAsync(ct);
        if (pendentes.Count == 0) return 0;

        // Episódios da mesma série buscam o mesmo termo — cacheia dentro do lote.
        var cache = new Dictionary<string, TmdbResultado?>();
        var processados = 0;

        foreach (var filme in pendentes)
        {
            var (titulo, ano) = MediaNomeParser.TituloParaBusca(filme.ArquivoPath, filme.Titulo);
            var serie = MediaNomeParser.EhEpisodio(filme.ArquivoPath, numerados);
            var overrideId = OverrideDe(_nomes?.TmdbOverrides ?? new Dictionary<string, int>(), filme.ArquivoPath, filme.Titulo, numerados);
            var chave = overrideId is int oid ? $"{(serie ? "tv" : "mv")}|#{oid}" : $"{(serie ? "tv" : "mv")}|{titulo}|{ano}";

            if (!cache.TryGetValue(chave, out var res))
            {
                try { res = await ResolverAsync(filme, titulo, ano, serie, overrideId, ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    // TMDB fora do ar / timeout: para o lote aqui. Os restantes (e este) ficam
                    // com MetadadosEm == null e o ExecuteAsync re-tenta em 10 min.
                    _logger.LogWarning(ex, "Metadados: TMDB indisponível — {N} filmes ficam pendentes.", pendentes.Count - processados);
                    break;
                }
                cache[chave] = res;
            }

            filme.TmdbId = res?.TmdbId;
            filme.TituloOriginal = res?.TituloOriginal;
            filme.PosterUrl = res?.PosterUrl;
            filme.Sinopse = res?.Sinopse;
            // Título limpo do TMDB só pra filme — episódio mantém o nome do arquivo, que o
            // parser de série/episódio ainda precisa pra achar o "SxxExx".
            if (!serie && res?.Titulo is { Length: > 0 } limpo) filme.Titulo = limpo;
            if (serie) filme.SerieTitulo = res?.Titulo;
            filme.MetadadosEm = DateTime.UtcNow;

            // Salva um por um: se um /scan concorrente apagou este filme no meio do lote,
            // o erro fica isolado neste filme e não descarta o trabalho (e a cota TMDB) dos outros.
            try { await db.SaveChangesAsync(ct); processados++; }
            catch (DbUpdateException)
            {
                db.Entry(filme).State = EntityState.Detached;
                _logger.LogInformation("Metadados: filme {Id} sumiu do banco durante o enriquecimento — ignorado.", filme.Id);
            }
        }

        var achados = pendentes.Take(processados).Count(f => f.TmdbId != null);
        _logger.LogInformation("Metadados: {N} filmes processados, {Achados} com match no TMDB.", processados, achados);
        return processados;
    }
}
