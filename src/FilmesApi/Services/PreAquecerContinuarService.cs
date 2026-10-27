namespace FilmesApi.Services;

/// <summary>
/// Deixa pronto, antes de abrir, o remux dos 3 primeiros da fila do controle (<see cref="FilaContinuar"/>: o que está pela metade ou o próximo episódio): a TV sem HLS
/// espera o remux inteiro terminar antes de tocar (533 MB ≈ 25 s; um filme de 4,8 GB ≈ 4 min de "Preparando áudio").
/// Só quando o servidor está ocioso e o arquivo cabe no teto do cache sem expulsar nada (<see cref="PoliticaPreAquecimento"/>);
/// o job cede o lugar se alguém precisar do encoder. <c>PreAquecerContinuarMinutos</c> (padrão 5; 0 desliga).
/// </summary>
public class PreAquecerContinuarService(IServiceScopeFactory escopos, IConfiguration config, ILogger<PreAquecerContinuarService> logger)
    : BackgroundService
{
    /// <summary>Quantos da frente da fila entram no pré-aquecimento (um aquecido por checagem).</summary>
    private const int Candidatos = 3;

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

            var filmes = escopo.ServiceProvider.GetRequiredService<FilmeService>();
            // um por vez: o que já está pronto (ou não precisa de remux) devolve false e passa a vez ao seguinte da fila
            foreach (var item in (await filmes.ObterFilaAsync()).Take(Candidatos))
            {
                var caminho = await filmes.ResolverCaminhoAsync(item.Filme.Id);
                if (caminho is not null && await transcode.PreAquecerSeNecessarioAsync(item.Filme.Id, caminho, item.Filme.Assistido, ct))
                    return item.Filme.Id;
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pré-aquecimento do continuar assistindo falhou — tenta de novo no próximo intervalo.");
            return null;
        }
    }
}
