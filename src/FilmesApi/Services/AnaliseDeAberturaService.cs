using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// Em segundo plano, acha a abertura dos episódios (botão "Pular abertura" do controle). Por temporada: primeiro o capítulo "Opening" do
/// próprio arquivo (exato, quando existe — <see cref="CapituloDeAbertura"/>); o resto pela comparação da impressão de áudio do começo com
/// os vizinhos (<see cref="DetectorDeAbertura"/>; ±1 e, só se nada achou, ±2). As impressões ficam guardadas (<see cref="ArmazemDeImpressoes"/>),
/// então melhorar o algoritmo e subir <see cref="VersaoAlgoritmo"/> reanalisa tudo em segundos, sem reler o disco. Só roda com o servidor
/// ocioso e uma temporada por vez. Config: <c>AberturaIntervaloMinutos</c> (15; 0 desliga), <c>AberturaAtrasoInicialSegundos</c> (180),
/// <c>AberturaOrcamentoMinutos</c> (4), <c>AberturaMinimoSegundos</c> (10), <c>AberturaUsarCapitulos</c> (true).
/// Origem 'pulo' (o Daniel pulou ali) e 'capitulo' nunca são sobrescritas por 'auto'; uma linha 'auto' de versão antiga só é trocada
/// quando há resultado novo (nunca é apagada por não achar de novo).
/// </summary>
public class AnaliseDeAberturaService(IServiceScopeFactory escopos, FfmpegOptions ffmpeg, IConfiguration config,
    ILogger<AnaliseDeAberturaService> logger, RegistroDeTarefas? tarefas = null) : BackgroundService
{
    /// <summary>Sobe quando o algoritmo de detecção melhora: linhas 'auto' de versão menor entram na fila de reanálise.</summary>
    public const int VersaoAlgoritmo = 2;

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
                analisados += await AnalisarTemporadaAsync(temporada, jaFeitos, filmes, escopo.ServiceProvider, db, ct);
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

    /// <summary>Esse episódio entra na análise? Sem linha; ou linha 'auto' de versão antiga; ou "não achou" antes de chegar episódio novo na
    /// temporada. Linha 'capitulo' ou 'pulo' nunca é refeita.</summary>
    internal static bool PrecisaAnalisar(AberturaEpisodio? linha, IEnumerable<FilmeResponse> temporada) =>
        linha is null
        || (linha.Origem == OrigemAbertura.Auto
            && (linha.Versao < VersaoAlgoritmo
                || (linha.InicioSegundos is null && temporada.Any(o => o.DataAdicionado > linha.AnalisadoEm))));

    /// <summary>Temporadas (2+ episódios) com algum episódio que <see cref="PrecisaAnalisar"/>.</summary>
    internal static List<List<FilmeResponse>> TemporadasPendentes(IReadOnlyList<FilmeResponse> catalogo, IReadOnlyDictionary<int, AberturaEpisodio> jaFeitos) =>
        catalogo.Where(f => f.EhEpisodio && !f.EhExtra && f.Temporada is not null && f.Episodio is not null && !string.IsNullOrEmpty(f.SerieChave))
            .GroupBy(f => (f.SerieChave, f.Temporada))
            .Select(g => g.OrderBy(f => f.Episodio).ThenBy(f => f.Id).ToList())
            .Where(eps => eps.Count >= 2 && eps.Any(f => PrecisaAnalisar(jaFeitos.GetValueOrDefault(f.Id), eps)))
            .ToList();

    private async Task<int> AnalisarTemporadaAsync(List<FilmeResponse> eps, Dictionary<int, AberturaEpisodio> jaFeitos,
        FilmeService filmes, IServiceProvider sp, AppDbContext db, CancellationToken ct)
    {
        var minimo = config.GetValue<double?>("AberturaMinimoSegundos") ?? 10;
        var caminhos = new string?[eps.Count];
        for (var i = 0; i < eps.Count; i++) caminhos[i] = await filmes.ResolverCaminhoAsync(eps[i].Id);
        var feitos = 0;
        var restantes = new HashSet<int>(Enumerable.Range(0, eps.Count).Where(i => PrecisaAnalisar(jaFeitos.GetValueOrDefault(eps[i].Id), eps)));

        // 1) capítulo "Opening" do arquivo: exato, vale mais que a comparação de áudio
        if (config.GetValue<bool?>("AberturaUsarCapitulos") ?? true)
        {
            var probe = sp.GetService<MediaProbeService>();
            foreach (var i in restantes.ToList())
            {
                if (probe is null || caminhos[i] is null) break;
                var info = await ProbeSeguroAsync(probe, caminhos[i]!, ct);
                if (CapituloDeAbertura.Abertura(info?.Capitulos) is not var (ini, fimCap)) continue;
                feitos += await GravarAsync(db, eps[i].Id, Math.Round(ini, 1), Math.Round(fimCap, 1), OrigemAbertura.Capitulo);
                restantes.Remove(i);
            }
        }

        // 2) comparação de áudio com os vizinhos (as impressões já guardadas não voltam ao disco)
        if (restantes.Count > 0)
        {
            var impressoes = new int[eps.Count][];
            for (var i = 0; i < eps.Count; i++)
                impressoes[i] = caminhos[i] is null ? [] : (await ArmazemDeImpressoes.ObterAsync(
                    db, ffmpeg, eps[i].Id, caminhos[i]!, ArmazemDeImpressoes.JanelaInicio, 0, ImpressaoDeAudio.Trecho.Inicio, ct)).Itens;

            foreach (var i in restantes)
            {
                if (impressoes[i].Length == 0) continue;   // sem áudio/ffmpeg falhou: tenta de novo na próxima rodada
                var achado = MelhorTrecho(i, impressoes, minimo);
                feitos += await GravarAsync(db, eps[i].Id, achado is null ? null : Math.Round(achado.InicioA, 1),
                    achado is null ? null : Math.Round(achado.FimA, 1), OrigemAbertura.Auto);
            }
        }

        db.ChangeTracker.Clear();
        foreach (var a in await db.Aberturas.AsNoTracking().Where(a => eps.Select(e => e.Id).Contains(a.FilmeId)).ToListAsync(ct)) jaFeitos[a.FilmeId] = a;
        return feitos;
    }

    /// <summary>Vizinhos ±1; só quando nenhum deles tem trecho comum, ±2 (recupera o episódio cujo vizinho imediato não tem abertura).</summary>
    internal static TrechoComum? MelhorTrecho(int i, int[][] impressoes, double minimoSegundos)
    {
        foreach (var raio in new[] { 1, 2 })
        {
            var melhor = new[] { i - raio, i + raio }.Where(j => j >= 0 && j < impressoes.Length && impressoes[j].Length > 0)
                .Select(j => DetectorDeAbertura.Encontrar(impressoes[i], impressoes[j], minimoSegundos))
                .OfType<TrechoComum>().Where(t => t.Duracao <= AberturaMaxSegundos)
                .OrderByDescending(t => t.Duracao).FirstOrDefault();
            if (melhor is not null) return melhor;
        }
        return null;
    }

    private async Task<MediaInfo?> ProbeSeguroAsync(MediaProbeService probe, string caminho, CancellationToken ct)
    {
        try { return await probe.InspecionarAsync(caminho, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }   // ffprobe ausente/quebrado: segue só com o áudio
    }

    /// <summary>Grava respeitando a precedência. 'auto' sem resultado ("não achou") não apaga uma marcação que já existe: só registra que foi reanalisada.
    /// Devolve 1 se mexeu na linha.</summary>
    private static async Task<int> GravarAsync(AppDbContext db, int filmeId, double? inicio, double? fim, string origem)
    {
        var linha = await db.Aberturas.FindAsync(filmeId);
        if (linha is not null && !OrigemAbertura.PodeSobrescrever(linha.Origem, origem)) return 0;
        if (linha is null) db.Aberturas.Add(linha = new AberturaEpisodio { FilmeId = filmeId });
        if (inicio is not null || linha.InicioSegundos is null)   // resultado novo, ou nada a perder
        {
            linha.InicioSegundos = inicio;
            linha.FimSegundos = fim;
            linha.Origem = origem;
        }
        linha.Versao = VersaoAlgoritmo;
        linha.AnalisadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return 1;
    }
}
