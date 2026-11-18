using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>Uma série seguida e o que o servidor mantém pronto dela agora: <c>Janela</c> (ids dos próximos episódios) e <c>Recentes</c> (assistidos há pouco, ainda protegidos).</summary>
public record SerieSeguidaResumo(string Chave, string Nome, int TamanhoDaJanela, DateTime Desde, IReadOnlyList<int> Janela, IReadOnlyList<int> Recentes);

/// <summary>
/// "Seguir série no cache": guarda quais séries são seguidas (tabela <c>SerieSeguida</c>) e, a cada atualização, calcula pela <see cref="PoliticaSeguirSerie"/>
/// a janela de episódios que o <see cref="CacheFixadoService"/> prepara e protege (a mesma máquina do "Manter no cache": ocioso, um por vez, teto, limite por rodada).
/// Configuração: <c>CacheSeguirJanela</c> (5) e <c>CacheSeguirLiberaAposHoras</c> (24).
/// </summary>
public class SeguirSerieService(IServiceScopeFactory escopos, IConfiguration config, ILogger<SeguirSerieService> logger)
{
    private sealed record Estado(IReadOnlyList<SerieSeguidaResumo> Seguidas, IReadOnlySet<int> Protegidos, IReadOnlyList<int> OrdemDePreparo);
    private volatile Estado _estado = new([], new HashSet<int>(), []);

    /// <summary>Muda a lista de séries seguidas (seguiu/deixou): o preparo acorda.</summary>
    public event Action? AoMudar;

    public IReadOnlyList<SerieSeguidaResumo> Seguidas => _estado.Seguidas;
    /// <summary>Janelas + assistidos recentes de todas as séries seguidas: nunca expulsos do cache.</summary>
    public IReadOnlySet<int> Protegidos => _estado.Protegidos;
    /// <summary>O que preparar, na ordem: a janela de cada série seguida.</summary>
    public IReadOnlyList<int> OrdemDePreparo => _estado.OrdemDePreparo;

    public int JanelaPadrao => Math.Max(1, config.GetValue<int?>("CacheSeguirJanela") ?? PoliticaSeguirSerie.JanelaPadrao);
    public TimeSpan LiberaApos => TimeSpan.FromHours(config.GetValue<double?>("CacheSeguirLiberaAposHoras") ?? PoliticaSeguirSerie.LiberaAposPadrao.TotalHours);

    /// <summary>Começa a seguir (ou muda a janela). false se a série não existe no catálogo.</summary>
    public async Task<bool> SeguirAsync(string chaveSerie, int? janela = null)
    {
        using var escopo = escopos.CreateScope();
        var filmes = escopo.ServiceProvider.GetRequiredService<FilmeService>();
        if (!(await filmes.ListarAsync()).Any(f => f.EhEpisodio && !f.EhExtra && f.SerieChave == chaveSerie)) return false;

        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var linha = await db.SeriesSeguidas.FirstOrDefaultAsync(s => s.ChaveSerie == chaveSerie);
        var n = Math.Clamp(janela ?? JanelaPadrao, 1, 30);
        if (linha is null) db.SeriesSeguidas.Add(new SerieSeguida { ChaveSerie = chaveSerie, Desde = DateTime.UtcNow, Janela = n });
        else linha.Janela = n;
        await db.SaveChangesAsync();
        logger.LogInformation("Seguir série no cache: '{Serie}' (janela de {N} episódios).", chaveSerie, n);
        await AtualizarAsync();
        AoMudar?.Invoke();
        return true;
    }

    /// <summary>Para de seguir. O que já está pronto fica no cache como qualquer outra cópia (sai pelas regras normais). false se não era seguida.</summary>
    public async Task<bool> DeixarAsync(string chaveSerie)
    {
        using var escopo = escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var linha = await db.SeriesSeguidas.FirstOrDefaultAsync(s => s.ChaveSerie == chaveSerie);
        if (linha is null) return false;
        db.SeriesSeguidas.Remove(linha);
        await db.SaveChangesAsync();
        logger.LogInformation("Seguir série no cache: '{Serie}' deixou de ser seguida.", chaveSerie);
        await AtualizarAsync();
        AoMudar?.Invoke();
        return true;
    }

    /// <summary>Recalcula as janelas (lê o catálogo e o que foi assistido). Sem séries seguidas, nem lê o catálogo. Nunca lança.</summary>
    public async Task AtualizarAsync()
    {
        try
        {
            using var escopo = escopos.CreateScope();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var seguidas = await db.SeriesSeguidas.AsNoTracking().OrderBy(s => s.Desde).ToListAsync();
            if (seguidas.Count == 0) { _estado = new([], new HashSet<int>(), []); return; }

            var catalogo = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ListarAsync();
            var chaves = seguidas.Select(s => s.ChaveSerie).ToHashSet();
            var episodios = catalogo.Where(f => f.EhEpisodio && !f.EhExtra && f.SerieChave is not null && chaves.Contains(f.SerieChave)).ToList();
            var assistidoEm = await db.Filmes.AsNoTracking()
                .Where(f => f.Assistido && f.AssistidoEm != null && episodios.Select(e => e.Id).Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, f => f.AssistidoEm);

            var agora = DateTime.UtcNow;
            var resumos = new List<SerieSeguidaResumo>();
            foreach (var s in seguidas)
            {
                var ordenados = episodios.Where(e => e.SerieChave == s.ChaveSerie)
                    .OrderBy(e => e.Temporada ?? 0).ThenBy(e => e.Episodio ?? 0).ThenBy(e => e.Id)
                    .Select(e => new EpisodioSeguido(e.Id, e.Assistido, assistidoEm.GetValueOrDefault(e.Id), !e.Assistido && e.PosicaoSegundos > 0))
                    .ToList();
                var nome = episodios.FirstOrDefault(e => e.SerieChave == s.ChaveSerie)?.Serie ?? s.ChaveSerie;
                resumos.Add(new SerieSeguidaResumo(s.ChaveSerie, nome, s.Janela, s.Desde,
                    PoliticaSeguirSerie.Janela(ordenados, s.Janela), PoliticaSeguirSerie.RecemAssistidos(ordenados, agora, LiberaApos)));
            }
            _estado = new Estado(resumos, resumos.SelectMany(r => r.Janela.Concat(r.Recentes)).ToHashSet(), [.. resumos.SelectMany(r => r.Janela)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Seguir série no cache: não consegui recalcular as janelas; sigo com as da última vez.");
        }
    }
}
