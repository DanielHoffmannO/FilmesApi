namespace FilmesApi.Services;

/// <summary>
/// Deixa o remux do "continuar assistindo" número 1 (o que o dono mais abre) pronto antes de ele abrir: a TV sem HLS
/// espera o remux inteiro terminar antes de tocar (533 MB ≈ 25 s; um filme de 4,8 GB ≈ 4 min de "Preparando áudio").
/// Só quando o servidor está ocioso e o arquivo cabe no teto do cache sem expulsar nada (<see cref="PoliticaPreAquecimento"/>);
/// o job cede o lugar se alguém precisar do encoder. <c>PreAquecerContinuarMinutos</c> (padrão 5; 0 desliga).
/// </summary>
public class PreAquecerContinuarService(IServiceScopeFactory escopos, IConfiguration config, ILogger<PreAquecerContinuarService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var minutos = config.GetValue<double?>("PreAquecerContinuarMinutos") ?? 5;
        if (minutos <= 0) return;

        try
        {
            // dá tempo do servidor terminar de subir (e do 1º scan) antes de mexer em disco por palpite
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue<double?>("PreAquecerAtrasoInicialSegundos") ?? 60), parar);
            using var relogio = new PeriodicTimer(TimeSpan.FromMinutes(minutos));
            do { await ExecutarUmaVezAsync(parar); } while (await relogio.WaitForNextTickAsync(parar));
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Uma checagem; nunca lança. Devolve o id do filme cujo remux começou, ou null se nada foi aquecido.</summary>
    internal async Task<int?> ExecutarUmaVezAsync(CancellationToken ct = default)
    {
        try
        {
            using var escopo = escopos.CreateScope();
            var transcode = escopo.ServiceProvider.GetRequiredService<HlsTranscodeService>();
            if (!transcode.Ocioso) return null;   // barato: antes de qualquer consulta

            var primeiro = (await escopo.ServiceProvider.GetRequiredService<ProgressoService>().ContinuarAssistindoAsync(limite: 1)).FirstOrDefault();
            if (primeiro is null) return null;

            var caminho = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ResolverCaminhoAsync(primeiro.Id);
            if (caminho is null) return null;

            // só vale pra quem precisa de remux (vídeo ok, áudio não); o que já toca direto ou vai por HLS não tem o que aquecer
            if (await transcode.AnalisarCompatibilidadeAsync(caminho, ct) != CompatibilidadeDireta.SoAudioIncompativel) return null;

            return transcode.PreAquecerRemux(primeiro.Id, caminho) ? primeiro.Id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pré-aquecimento do continuar assistindo falhou — tenta de novo no próximo intervalo.");
            return null;
        }
    }
}
