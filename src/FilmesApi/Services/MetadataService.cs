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

    public MetadataService(IServiceScopeFactory escopos, TmdbService tmdb, ILogger<MetadataService> logger)
    {
        _escopos = escopos;
        _tmdb = tmdb;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_tmdb.Habilitado) return;

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int processados;
            try { processados = await ProcessarLoteAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enriquecimento de metadados: lote falhou.");
                processados = 0;
            }

            // Nada pendente → dorme bastante (novos arquivos aparecem no próximo scan).
            var pausa = processados > 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromHours(6);
            try { await Task.Delay(pausa, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<int> ProcessarLoteAsync(CancellationToken ct)
    {
        using var escopo = _escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var pendentes = await db.Filmes
            .Where(f => f.MetadadosEm == null && f.ArquivoPath != null)
            .OrderBy(f => f.Id)
            .Take(TamanhoLote)
            .ToListAsync(ct);
        if (pendentes.Count == 0) return 0;

        // Episódios da mesma série buscam o mesmo termo — cacheia dentro do lote.
        var cache = new Dictionary<string, TmdbResultado?>();

        foreach (var filme in pendentes)
        {
            var (titulo, ano) = MediaNomeParser.TituloParaBusca(filme.ArquivoPath, filme.Titulo);
            var serie = MediaNomeParser.EhEpisodio(filme.ArquivoPath);
            var chave = $"{(serie ? "tv" : "mv")}|{titulo}|{ano}";

            if (!cache.TryGetValue(chave, out var res))
            {
                res = string.IsNullOrWhiteSpace(titulo) ? null : await _tmdb.BuscarAsync(titulo, ano, serie, ct);
                cache[chave] = res;
                try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); }
                catch (OperationCanceledException) { break; }
            }

            filme.TmdbId = res?.TmdbId;
            filme.TituloOriginal = res?.TituloOriginal;
            filme.PosterUrl = res?.PosterUrl;
            filme.Sinopse = res?.Sinopse;
            filme.MetadadosEm = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        var achados = pendentes.Count(f => f.TmdbId != null);
        _logger.LogInformation("Metadados: {N} filmes processados, {Achados} com match no TMDB.", pendentes.Count, achados);
        return pendentes.Count;
    }
}
