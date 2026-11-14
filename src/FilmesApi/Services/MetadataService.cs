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

    public MetadataService(IServiceScopeFactory escopos, TmdbService tmdb, ILogger<MetadataService> logger,
        RegistroDeTarefas? tarefas = null, NomesPersonalizadosService? nomes = null)
    {
        _nomes = nomes;
        if (nomes is not null)
            nomes.TmdbMudou += () => { try { _acordar.Release(); } catch (SemaphoreFullException) { /* já acordado */ } };
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
    internal static IEnumerable<string> ChavesDeOverride(string? arquivoPath, string? tituloFallback)
    {
        var (titulo, _) = MediaNomeParser.TituloParaBusca(arquivoPath, tituloFallback);
        if (!string.IsNullOrWhiteSpace(titulo)) yield return MediaNomeParser.NormalizarBusca(titulo);
        if (MediaNomeParser.EhEpisodio(arquivoPath))
        {
            var s = MediaNomeParser.ChaveSerie(arquivoPath);
            if (!string.IsNullOrWhiteSpace(s)) yield return MediaNomeParser.NormalizarBusca(s);
        }
    }

    /// <summary>Override vigente do item, se houver.</summary>
    internal static int? OverrideDe(IReadOnlyDictionary<string, int> overrides, string? arquivoPath, string? tituloFallback)
    {
        if (overrides.Count == 0) return null;
        foreach (var k in ChavesDeOverride(arquivoPath, tituloFallback))
            if (overrides.TryGetValue(k, out var id)) return id;
        return null;
    }

    /// <summary>Re-enriquece só os itens cujo override de tmdbId é novo (ou mudou): zera o <c>MetadadosEm</c> deles.</summary>
    private async Task InvalidarOverridesNovosAsync(AppDbContext db, CancellationToken ct)
    {
        var overrides = _nomes?.TmdbOverrides;
        if (overrides is null || overrides.Count == 0) return;

        var candidatos = await db.Filmes.Where(f => f.ArquivoPath != null && f.MetadadosEm != null).ToListAsync(ct);
        var invalidados = 0;
        foreach (var f in candidatos)
        {
            if (OverrideDe(overrides, f.ArquivoPath, f.Titulo) is not int id || f.TmdbId == id) continue;
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
    private async Task ReenriquecerTitulosNaoLatinosAsync(AppDbContext db, CancellationToken ct)
    {
        if (_varreuNaoLatinos || !_tmdb.Habilitado) return;
        _varreuNaoLatinos = true;

        var candidatos = await db.Filmes.Where(f => f.ArquivoPath != null && f.MetadadosEm != null).ToListAsync(ct);
        var voltam = candidatos.Where(f => TmdbService.TemEscritaNaoLatina(f.Titulo) && !MediaNomeParser.EhEpisodio(f.ArquivoPath)).ToList();
        if (voltam.Count == 0) return;
        foreach (var f in voltam) f.MetadadosEm = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Metadados: {N} título(s) em escrita não latina voltam pro enriquecimento (título em português).", voltam.Count);
    }

    private bool _varreuSeries;

    /// <summary>Uma vez por execução: episódio já enriquecido antes de o serviço guardar o nome da série no TMDB volta pro enriquecimento.</summary>
    private async Task ReenriquecerSeriesSemNomeAsync(AppDbContext db, CancellationToken ct)
    {
        if (_varreuSeries || !_tmdb.Habilitado) return;
        _varreuSeries = true;

        var candidatos = await db.Filmes.Where(f => f.ArquivoPath != null && f.MetadadosEm != null && f.SerieTitulo == null).ToListAsync(ct);
        var voltam = candidatos.Where(f => MediaNomeParser.EhEpisodio(f.ArquivoPath)).ToList();
        if (voltam.Count == 0) return;
        foreach (var f in voltam) f.MetadadosEm = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Metadados: {N} episódio(s) voltam pro enriquecimento pra guardar o nome da série no TMDB.", voltam.Count);
    }

    private async Task<int> ProcessarLoteAsync(CancellationToken ct)
    {
        using var escopo = _escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        await InvalidarOverridesNovosAsync(db, ct);
        await ReenriquecerTitulosNaoLatinosAsync(db, ct);
        await ReenriquecerSeriesSemNomeAsync(db, ct);

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
            var serie = MediaNomeParser.EhEpisodio(filme.ArquivoPath);
            var overrideId = OverrideDe(_nomes?.TmdbOverrides ?? new Dictionary<string, int>(), filme.ArquivoPath, filme.Titulo);
            var chave = overrideId is int oid ? $"{(serie ? "tv" : "mv")}|#{oid}" : $"{(serie ? "tv" : "mv")}|{titulo}|{ano}";

            if (!cache.TryGetValue(chave, out var res))
            {
                try
                {
                    if (overrideId is int idForcado)
                    {
                        // id inexistente (404) devolve null: cai na busca normal em vez de deixar o item sem metadados
                        res = await _tmdb.BuscarPorIdAsync(idForcado, serie, ct);
                        if (res is null) await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                    }
                    if (res is null)
                        res = string.IsNullOrWhiteSpace(titulo) ? null : await _tmdb.BuscarAsync(titulo, ano, serie, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    // TMDB fora do ar / timeout: para o lote aqui. Os restantes (e este) ficam
                    // com MetadadosEm == null e o ExecuteAsync re-tenta em 10 min.
                    _logger.LogWarning(ex, "Metadados: TMDB indisponível — {N} filmes ficam pendentes.", pendentes.Count - processados);
                    break;
                }
                cache[chave] = res;
                try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); }
                catch (OperationCanceledException) { break; }
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
