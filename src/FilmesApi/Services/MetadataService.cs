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
            TimeSpan pausa;
            try
            {
                var processados = await ProcessarLoteAsync(stoppingToken);
                // Processou algo → segue logo pro próximo lote. Nada pendente → dorme bastante
                // (novos arquivos só aparecem no próximo scan).
                pausa = processados > 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromHours(6);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Falha de rede/DB/schema — tenta de novo em 10 min, não daqui a 6h.
                _logger.LogWarning(ex, "Enriquecimento de metadados: lote falhou, nova tentativa em 10 min.");
                pausa = TimeSpan.FromMinutes(10);
            }

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
        var processados = 0;

        foreach (var filme in pendentes)
        {
            var (titulo, ano) = MediaNomeParser.TituloParaBusca(filme.ArquivoPath, filme.Titulo);
            var serie = MediaNomeParser.EhEpisodio(filme.ArquivoPath);
            var chave = $"{(serie ? "tv" : "mv")}|{titulo}|{ano}";

            if (!cache.TryGetValue(chave, out var res))
            {
                try
                {
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
