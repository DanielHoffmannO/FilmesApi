namespace FilmesApi.Services;

/// <summary>
/// Roda o mesmo scan do botão "Scan Mídia" de tempos em tempos, pra download novo aparecer sozinho.
/// <c>ScanIntervaloMinutos</c> (padrão 10; 0 desliga). Não concorre com um scan manual: o próprio
/// <see cref="FilmeService.ScanMediaAsync"/> serializa os dois. Atenção: varrer a árvore acorda HD
/// externo em repouso — num backup que deveria dormir, aumente o intervalo ou desligue.
/// </summary>
public class ScanPeriodicoService(IServiceScopeFactory escopos, IConfiguration config, ILogger<ScanPeriodicoService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var minutos = config.GetValue<double?>("ScanIntervaloMinutos") ?? 10;
        if (minutos <= 0)
        {
            logger.LogInformation("Scan automático desligado (ScanIntervaloMinutos=0).");
            return;
        }

        using var relogio = new PeriodicTimer(TimeSpan.FromMinutes(minutos));
        try
        {
            while (await relogio.WaitForNextTickAsync(parar)) await ExecutarUmaVezAsync();
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Um scan; nunca lança (um scan que falha não pode derrubar o timer nem o app).</summary>
    internal async Task ExecutarUmaVezAsync()
    {
        try
        {
            using var escopo = escopos.CreateScope();
            var r = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ScanMediaAsync();
            if (r.Importados > 0 || r.Removidos > 0 || r.TitulosLimpos > 0)
                logger.LogInformation("Scan automático: {Novos} novo(s), {Removidos} removido(s), {Limpos} título(s) limpo(s).",
                    r.Importados, r.Removidos, r.TitulosLimpos);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scan automático falhou — tenta de novo no próximo intervalo.");
        }
    }
}
