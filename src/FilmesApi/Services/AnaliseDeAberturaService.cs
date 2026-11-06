using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// Em segundo plano, acha a abertura dos episódios (botão "Pular abertura" do controle): por temporada, tira a impressão de
/// áudio do começo de cada episódio e compara com os vizinhos (<see cref="DetectorDeAbertura"/>). Só roda com o servidor ocioso
/// (a TV tem prioridade) e uma temporada por vez. <c>AberturaIntervaloMinutos</c> (padrão 15; 0 desliga),
/// <c>AberturaAtrasoInicialSegundos</c> (180), <c>AberturaOrcamentoMinutos</c> (4: quanto cada rodada pode gastar).
/// Episódio sem abertura achada fica marcado e só é reanalisado se chegar episódio novo na temporada.
/// </summary>
public class AnaliseDeAberturaService(IServiceScopeFactory escopos, FfmpegOptions ffmpeg, IConfiguration config,
    ILogger<AnaliseDeAberturaService> logger, RegistroDeTarefas? tarefas = null) : BackgroundService
{
    private const double AberturaMaxSegundos = 180;

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var minutos = config.GetValue<double?>("AberturaIntervaloMinutos") ?? 15;
        if (minutos <= 0) { tarefas?.Declarar("abertura", ligada: false); return; }
        tarefas?.Declarar("abertura");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue<double?>("AberturaAtrasoInicialSegundos") ?? 180), parar);
            using var relogio = new PeriodicTimer(TimeSpan.FromMinutes(minutos));
            do { await ExecutarUmaVezAsync(parar); } while (await relogio.WaitForNextTickAsync(parar));
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Uma rodada: temporadas pendentes até o orçamento de tempo ou até o servidor deixar de estar ocioso. Nunca lança.
    /// Devolve quantos episódios foram analisados.</summary>
    internal async Task<int> ExecutarUmaVezAsync(CancellationToken ct = default)
    {
        var medicao = tarefas?.Iniciar("abertura");
        var analisados = 0;
        try
        {
            using var escopo = escopos.CreateScope();
            var transcode = escopo.ServiceProvider.GetRequiredService<HlsTranscodeService>();
            var filmes = escopo.ServiceProvider.GetRequiredService<FilmeService>();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

            var fim = DateTime.UtcNow.AddMinutes(config.GetValue<double?>("AberturaOrcamentoMinutos") ?? 4);
            var jaFeitos = (await db.Aberturas.AsNoTracking().ToListAsync(ct)).ToDictionary(a => a.FilmeId);
            var pendentes = TemporadasPendentes(await filmes.ListarAsync(), jaFeitos);

            foreach (var temporada in pendentes)
            {
                if (!transcode.Ocioso || DateTime.UtcNow > fim) break;
                analisados += await AnalisarTemporadaAsync(temporada, jaFeitos, filmes, db, ct);
            }
            medicao?.Ok(pendentes.Count == 0 ? "nada pendente" : $"{analisados} episódio(s) analisado(s), {pendentes.Count} temporada(s) na fila");
            if (analisados > 0) logger.LogInformation("Aberturas: {N} episódio(s) analisado(s).", analisados);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            medicao?.Falhou(ex);
            logger.LogWarning(ex, "Análise de aberturas falhou — tenta de novo no próximo intervalo.");
        }
        return analisados;
    }

    /// <summary>Temporadas (2+ episódios) com algum episódio sem linha, ou marcado "sem abertura" antes de chegar episódio novo.</summary>
    internal static List<List<FilmeResponse>> TemporadasPendentes(IReadOnlyList<FilmeResponse> catalogo, IReadOnlyDictionary<int, AberturaEpisodio> jaFeitos) =>
        catalogo.Where(f => f.EhEpisodio && !f.EhExtra && f.Temporada is not null && f.Episodio is not null && !string.IsNullOrEmpty(f.SerieChave))
            .GroupBy(f => (f.SerieChave, f.Temporada))
            .Select(g => g.OrderBy(f => f.Episodio).ThenBy(f => f.Id).ToList())
            .Where(eps => eps.Count >= 2 && eps.Any(f =>
                !jaFeitos.TryGetValue(f.Id, out var a)
                || (a.InicioSegundos is null && eps.Any(o => o.DataAdicionado > a.AnalisadoEm))))
            .ToList();

    private async Task<int> AnalisarTemporadaAsync(List<FilmeResponse> eps, Dictionary<int, AberturaEpisodio> jaFeitos,
        FilmeService filmes, AppDbContext db, CancellationToken ct)
    {
        var impressoes = new int[eps.Count][];
        for (var i = 0; i < eps.Count; i++)
        {
            var caminho = await filmes.ResolverCaminhoAsync(eps[i].Id);
            impressoes[i] = caminho is null ? [] : await ImpressaoDeAudio.ObterAsync(ffmpeg.Ffmpeg, caminho, ct) ?? [];
        }

        var feitos = 0;
        for (var i = 0; i < eps.Count; i++)
        {
            if (jaFeitos.TryGetValue(eps[i].Id, out var antigo) && !(antigo.InicioSegundos is null && eps.Any(o => o.DataAdicionado > antigo.AnalisadoEm)))
                continue;
            if (impressoes[i].Length == 0) continue;   // sem áudio/ffmpeg falhou: tenta de novo na próxima rodada

            var melhor = new[] { i - 1, i + 1 }.Where(j => j >= 0 && j < eps.Count && impressoes[j].Length > 0)
                .Select(j => DetectorDeAbertura.Encontrar(impressoes[i], impressoes[j]))
                .OfType<TrechoComum>().Where(t => t.Duracao <= AberturaMaxSegundos)
                .OrderByDescending(t => t.Duracao).FirstOrDefault();

            var linha = await db.Aberturas.FindAsync([eps[i].Id], ct);
            if (linha is null) db.Aberturas.Add(linha = new AberturaEpisodio { FilmeId = eps[i].Id });
            linha.InicioSegundos = melhor is null ? null : Math.Round(melhor.InicioA, 1);
            linha.FimSegundos = melhor is null ? null : Math.Round(melhor.FimA, 1);
            linha.AnalisadoEm = DateTime.UtcNow;
            feitos++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return feitos;
    }
}
