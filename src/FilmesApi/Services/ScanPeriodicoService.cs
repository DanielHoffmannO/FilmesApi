namespace FilmesApi.Services;

/// <summary>
/// Roda o mesmo scan do botão "Scan Mídia" de tempos em tempos, pra download novo aparecer sozinho.
/// <c>ScanIntervaloMinutos</c> (padrão 10; 0 desliga). Não concorre com um scan manual: o próprio
/// <see cref="FilmeService.ScanMediaAsync"/> serializa os dois. Atenção: varrer a árvore acorda HD
/// externo em repouso — num backup que deveria dormir, aumente o intervalo ou desligue. Também roda sob demanda:
/// <see cref="Solicitar"/> (webhook do Sonarr/Radarr), com rajadas coalescidas e nunca dois scans ao mesmo tempo.
/// </summary>
public class ScanPeriodicoService(IServiceScopeFactory escopos, IConfiguration config, ILogger<ScanPeriodicoService> logger,
    RegistroDeTarefas? tarefas = null)
    : BackgroundService
{
    // ─── Scan sob demanda (webhook do Sonarr/Radarr) ────────────────────────────────────────────────
    // Pedidos de Solicitar() são coalescidos por DebounceDeScan e rodados por uma "bomba" própria, que NÃO depende do
    // timer periódico (ele pode estar desligado, ScanIntervaloMinutos=0). Nunca há dois scans ao mesmo tempo (_umPorVez).
    private readonly DebounceDeScan _pedidos = DebounceDeScan.De(config);
    private readonly System.Diagnostics.Stopwatch _relogio = System.Diagnostics.Stopwatch.StartNew();   // monotônico (sem RTC, o relógio de parede pula)
    private readonly SemaphoreSlim _umPorVez = new(1, 1);
    private readonly CancellationTokenSource _encerrando = new();
    private int _bomba;

    /// <summary>Pede um scan "agora" (ex.: o *arr acabou de importar um arquivo). Volta na hora; o scan roda em segundo plano
    /// depois da pausa do debounce (<c>Webhook:DebounceSegundos</c>, 12 s), e uma rajada de pedidos vira um scan só.</summary>
    public void Solicitar()
    {
        if (_encerrando.IsCancellationRequested) return;
        _pedidos.Solicitar(_relogio.Elapsed);
        if (Interlocked.Exchange(ref _bomba, 1) == 0) _ = Task.Run(BombaAsync);
    }

    /// <summary>Sem pedido esperando e sem bomba rodando (só pros testes).</summary>
    internal bool Ocioso => !_pedidos.Pendente && Volatile.Read(ref _bomba) == 0;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _encerrando.Cancel();
        return base.StopAsync(cancellationToken);
    }

    private async Task BombaAsync()
    {
        while (true)
        {
            try
            {
                while (_pedidos.TempoAteVencer(_relogio.Elapsed) is { } falta)
                {
                    if (falta > TimeSpan.Zero) await Task.Delay(falta, _encerrando.Token);
                    else if (_pedidos.TentarColher(_relogio.Elapsed)) await RodarScanAsync("por webhook", esperarVez: true);
                }
            }
            catch (OperationCanceledException) { /* servidor parando */ }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scan por webhook: a fila de pedidos falhou — o próximo pedido recomeça.");
                Volatile.Write(ref _bomba, 0);
                return;
            }

            // pedido que chegou entre o último TempoAteVencer e o fim da bomba não pode ficar sem ninguém pra rodá-lo
            Volatile.Write(ref _bomba, 0);
            if (_encerrando.IsCancellationRequested || !_pedidos.Pendente || Interlocked.Exchange(ref _bomba, 1) == 1) return;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var minutos = config.GetValue<double?>("ScanIntervaloMinutos") ?? 10;
        if (minutos <= 0)
        {
            tarefas?.Declarar("scan", ligada: false);
            logger.LogInformation("Scan automático desligado (ScanIntervaloMinutos=0).");
            return;
        }

        tarefas?.Declarar("scan");
        using var relogio = new PeriodicTimer(TimeSpan.FromMinutes(minutos));
        try
        {
            while (await relogio.WaitForNextTickAsync(parar)) await ExecutarUmaVezAsync();
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Um scan; nunca lança (um scan que falha não pode derrubar o timer nem o app).</summary>
    internal Task ExecutarUmaVezAsync() => RodarScanAsync("automático", esperarVez: false);

    // esperarVez: o do timer PULA se já há um scan rodando (aquele já cobre o que ele ia ver); o do webhook espera a vez,
    // porque o scan em andamento pode ter começado antes do arquivo novo existir.
    private async Task RodarScanAsync(string origem, bool esperarVez)
    {
        if (esperarVez) await _umPorVez.WaitAsync(_encerrando.Token);
        else if (!await _umPorVez.WaitAsync(0))
        {
            logger.LogDebug("Scan {Origem}: já há um scan rodando — pulado.", origem);
            return;
        }

        var medicao = tarefas?.Iniciar("scan");
        try
        {
            using var escopo = escopos.CreateScope();
            var r = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ScanMediaAsync();
            if (r.Importados > 0 || r.Removidos > 0 || r.TitulosLimpos > 0)
                logger.LogInformation("Scan {Origem}: {Novos} novo(s), {Removidos} removido(s), {Limpos} título(s) limpo(s).",
                    origem, r.Importados, r.Removidos, r.TitulosLimpos);
            medicao?.Ok($"{r.Importados} novo(s), {r.Removidos} removido(s), {r.TitulosLimpos} título(s) limpo(s)");
        }
        catch (Exception ex)
        {
            medicao?.Falhou(ex);
            logger.LogWarning(ex, "Scan {Origem} falhou — tenta de novo no próximo intervalo.", origem);
        }
        finally { _umPorVez.Release(); }
    }
}
