using FilmesApi.Data;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// Varre o cache de HLS/remux de tempos em tempos e tira o que não serve mais (regras em <see cref="LimpezaDeCache"/>).
/// <c>LimpezaCacheMinutos</c> (padrão 60; 0 desliga), <c>LimpezaCacheCarenciaMinutos</c> (120: quem revê um episódio
/// assistido não perde o cache na hora), <c>HlsValidadeDias</c> (14), <c>LimpezaCacheAtrasoInicialSegundos</c> (120: dá
/// tempo ao 1º scan de povoar o catálogo — sem ele tudo pareceria órfão).
/// </summary>
public class LimpezaCacheService(IServiceScopeFactory escopos, HlsTranscodeService transcode, IConfiguration config,
    ILogger<LimpezaCacheService> logger) : BackgroundService
{
    private readonly object _trava = new();
    private (InventarioCache? Inventario, DateTime Quando) _inventario;

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var minutos = config.GetValue<double?>("LimpezaCacheMinutos") ?? 60;
        if (minutos <= 0) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue<double?>("LimpezaCacheAtrasoInicialSegundos") ?? 120), parar);
            using var relogio = new PeriodicTimer(TimeSpan.FromMinutes(minutos));
            do { await ExecutarUmaVezAsync(); } while (await relogio.WaitForNextTickAsync(parar));
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Uma varredura; nunca lança. Loga o resumo quando apagou algo.</summary>
    internal async Task<ResumoLimpeza> ExecutarUmaVezAsync()
    {
        try
        {
            var op = new OpcoesLimpeza(
                TimeSpan.FromMinutes(config.GetValue<double?>("LimpezaCacheCarenciaMinutos") ?? 120),
                TimeSpan.FromDays(config.GetValue<double?>("HlsValidadeDias") ?? 14));
            var assistidos = await AssistidosAsync();
            var resumo = LimpezaDeCache.Varrer(transcode.PastaHls, transcode.PastaRemux, assistidos, DateTime.UtcNow, op, transcode.GanchosDeCache());
            if (resumo.Itens > 0)
                logger.LogInformation("Limpeza do cache: {Itens} itens, {Gb:F1} GB.", resumo.Itens, resumo.Bytes / 1073741824.0);
            return resumo;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Limpeza do cache falhou — tenta de novo no próximo intervalo.");
            return new ResumoLimpeza(0, 0);
        }
    }

    /// <summary>O que o cache guarda por categoria. Refeito no máximo a cada 20 s (a página de status faz poll).</summary>
    public async Task<InventarioCache> InventarioAsync()
    {
        lock (_trava)
            if (_inventario.Inventario is { } pronto && DateTime.UtcNow - _inventario.Quando < TimeSpan.FromSeconds(20)) return pronto;

        var assistidos = await AssistidosAsync();
        var novo = LimpezaDeCache.Inventariar(transcode.PastaHls, transcode.PastaRemux, assistidos, DateTime.UtcNow,
            transcode.GanchosDeCache().UltimoAcesso, transcode.TetoHlsBytes, transcode.TetoRemuxBytes);
        lock (_trava) _inventario = (novo, DateTime.UtcNow);
        return novo;
    }

    private async Task<Dictionary<int, bool>> AssistidosAsync()
    {
        using var escopo = escopos.CreateScope();
        return await escopo.ServiceProvider.GetRequiredService<AppDbContext>().Filmes.AsNoTracking()
            .ToDictionaryAsync(f => f.Id, f => f.Assistido);
    }
}
