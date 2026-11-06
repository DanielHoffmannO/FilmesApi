using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// O que o controle "aprende" com o que o Daniel faz e o que a TV executa sozinha: sabe a abertura/interruptores do episódio em exibição
/// (<see cref="ContextoDoFilme"/>, que vai no snapshot do player) e, quando ele pula a abertura apertando « 30s » (uma rajada, ver
/// <see cref="PlayerStateService.RajadaConcluida"/>), confere se aquele trecho é mesmo abertura (<see cref="AprendizadoDePulo"/>), grava a abertura com
/// origem 'pulo', procura o mesmo áudio nos outros episódios da temporada e liga o pulo automático dela. Só liga; quem desliga é o Daniel.
/// </summary>
public class AutomacaoDoPlayer(IServiceScopeFactory escopos, PlayerStateService estado, PreferenciasTemporadaService temporadas,
    FfmpegOptions ffmpeg, IConfiguration config, ILogger<AutomacaoDoPlayer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct) { estado.RajadaConcluida += OnRajada; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { estado.RajadaConcluida -= OnRajada; return Task.CompletedTask; }

    /// <summary>Uma aprendizagem por vez: rajadas seguidas esperam a anterior (cada uma lê áudio do mesmo disco que a TV usa).</summary>
    private readonly SemaphoreSlim _umPorVez = new(1, 1);

    /// <summary>Quantos vizinhos de cada lado têm a impressão gerada na hora; os demais da temporada só entram como modelo se a impressão já estiver guardada.</summary>
    internal const int RaioDeVizinhos = 2;

    private void OnRajada(RajadaDePulo r) => _ = Task.Run(() => AprenderAsync(r));

    /// <summary>Escolhe o filme no player e já monta o contexto dele (abertura conhecida + interruptores da temporada). O único caminho pra "selecionar".</summary>
    public async Task SelecionarAsync(int filmeId)
    {
        estado.Selecionar(filmeId);
        await AtualizarContextoAsync(filmeId);
    }

    /// <summary>Monta o contexto do episódio (abertura conhecida + interruptores da temporada) e entrega ao estado do player.</summary>
    public async Task AtualizarContextoAsync(int filmeId)
    {
        try
        {
            using var escopo = escopos.CreateScope();
            var filmes = escopo.ServiceProvider.GetRequiredService<FilmeService>();
            var filme = await filmes.ObterAsync(filmeId);
            var abertura = await filmes.ObterAberturaAsync(filmeId);
            var lig = filme is { EhEpisodio: true, SerieChave: { } chave, Temporada: { } t } ? temporadas.Da(chave, t) : null;
            estado.DefinirContexto(filmeId, new ContextoDoFilme(
                abertura?.Inicio, abertura?.Fim, lig?.PularAbertura ?? false, lig?.ProximoAutomatico ?? false,
                await filmes.ObterCreditosAsync(filmeId), filme?.SerieChave, filme?.Temporada,
                config.GetValue<int?>("ProximoAutomaticoContagemSegundos") ?? 5));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Não consegui montar o contexto do filme {Id}.", filmeId);
        }
    }

    /// <summary>Liga o pulo automático da temporada do episódio em exibição. Se estava desligado, avisa (uma vez) e devolve true.</summary>
    public async Task<bool> LigarPuloAutomaticoAsync(int filmeId, string? textoDoAviso = null)
    {
        using var escopo = escopos.CreateScope();
        var filme = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ObterAsync(filmeId);
        if (filme is not { EhEpisodio: true, SerieChave: { } chave, Temporada: { } t }) return false;
        var ligou = temporadas.Ligar(chave, t, abertura: true);
        if (ligou) estado.Avisar("abertura", textoDoAviso ?? $"Pulo automático da abertura ligado na temporada {t}.", "desligar");
        await AtualizarContextoAsync(filmeId);
        return ligou;
    }

    /// <summary>Liga o próximo episódio automático da temporada do episódio em exibição (o Daniel apertou "próximo" no fim). Avisa uma vez.</summary>
    public async Task<bool> LigarProximoAutomaticoAsync(int filmeId)
    {
        using var escopo = escopos.CreateScope();
        var filme = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ObterAsync(filmeId);
        if (filme is not { EhEpisodio: true, SerieChave: { } chave, Temporada: { } t }) return false;
        if (!temporadas.Ligar(chave, t, abertura: false)) return false;
        estado.Avisar("proximo", $"Próximo automático ligado na temporada {t}.", "desligar-proximo");
        return true;
    }

    /// <summary>"Próximo episódio" apertado no controle: toca o próximo e, se foi no fim (<see cref="ProximoManual"/>), liga o próximo automático
    /// da temporada. Devolve o id do próximo, ou null se não há. Apertar no meio do episódio só toca — nada é aprendido.</summary>
    public async Task<(int? ProximoId, bool Aprendeu)> ProximoManualAsync()
    {
        if (estado.FilmeId is not int id) return (null, false);
        using var escopo = escopos.CreateScope();
        var prox = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ProximoEpisodioAsync(id);
        if (prox is null) return (null, false);

        var acabou = estado.JaAcabou(config.GetValue<double?>("ProximoRestanteSegundos") ?? ProximoManual.RestantePadraoSegundos);
        var aprendeu = acabou && await LigarProximoAutomaticoAsync(id);
        await SelecionarAsync(prox.Id);
        return (prox.Id, aprendeu);
    }

    /// <summary>Uma rajada de avanços terminou: era a abertura? Nunca lança.</summary>
    internal async Task AprenderAsync(RajadaDePulo r, CancellationToken ct = default)
    {
        try { await _umPorVez.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            using var escopo = escopos.CreateScope();
            var filmes = escopo.ServiceProvider.GetRequiredService<FilmeService>();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

            var catalogo = await filmes.ListarAsync();
            var filme = catalogo.FirstOrDefault(f => f.Id == r.FilmeId);
            if (filme is not { EhEpisodio: true, EhExtra: false, Temporada: { } temporada, SerieChave: { } chave }) return;
            var eps = catalogo.Where(f => f.EhEpisodio && !f.EhExtra && f.Temporada == temporada && f.SerieChave == chave)
                .OrderBy(f => f.Episodio).ThenBy(f => f.Id).ToList();
            if (eps.Count < 2) return;   // temporada de um episódio só: nada pra comparar

            var minimoPulo = config.GetValue<double?>("AberturaMinimoPuloSegundos") ?? 4;
            var linha = await db.Aberturas.AsNoTracking().FirstOrDefaultAsync(a => a.FilmeId == r.FilmeId, ct);

            // já conhecida e o pulo cobre ela: só confirma (liga o pulo automático), sem reanalisar
            if (linha is { InicioSegundos: double ini0, FimSegundos: double fim0 } && AprendizadoDePulo.CobreAConhecida(r, ini0, fim0))
            {
                await LigarPuloAutomaticoAsync(r.FilmeId, $"Abertura confirmada na temporada {temporada}. Pulo automático ligado.");
                return;
            }

            var caminho = await filmes.ResolverCaminhoAsync(r.FilmeId);
            if (caminho is null) return;
            var (inicioJanela, duracaoJanela) = AprendizadoDePulo.Janela(r);
            var janela = await ImpressaoDeAudio.ObterAsync(ffmpeg.Ffmpeg, caminho, new ImpressaoDeAudio.Trecho(inicioJanela, duracaoJanela), ct);
            if (janela is null || janela.Length == 0) return;

            // impressões: dos vizinhos próximos (±RaioDeVizinhos) as guardadas ou geradas agora (ffmpeg em prioridade baixa); dos demais, só as
            // que já estão guardadas — o resto da temporada o serviço de fundo completa quando o servidor está ocioso
            var pos = eps.FindIndex(e => e.Id == r.FilmeId);
            var impressoes = new int[eps.Count][];
            var caminhos = new string?[eps.Count];
            for (var i = 0; i < eps.Count; i++)
            {
                var cam = caminhos[i] = i == pos ? null : await filmes.ResolverCaminhoAsync(eps[i].Id);
                impressoes[i] = cam is null ? []
                    : Math.Abs(i - pos) <= RaioDeVizinhos
                        ? (await ArmazemDeImpressoes.ObterAsync(db, ffmpeg, eps[i].Id, cam, ArmazemDeImpressoes.JanelaInicio, 0, ImpressaoDeAudio.Trecho.Inicio, ct)).Itens
                        : await ArmazemDeImpressoes.SoGuardadaAsync(db, eps[i].Id, cam, ArmazemDeImpressoes.JanelaInicio, ct);
            }
            var vizinhos = new[] { pos - 1, pos + 1, pos - 2, pos + 2 }.Where(j => j >= 0 && j < eps.Count).Select(j => impressoes[j]).ToList();

            var validado = AprendizadoDePulo.Validar(janela, inicioJanela, r, vizinhos, minimoPulo);
            if (validado is null)
            {
                logger.LogDebug("Pulo de {Ini:F0}s a {Fim:F0}s no filme {Id}: sem áudio em comum com os vizinhos — era só uma cena pulada.", r.PosAntes, r.PosFinal, r.FilmeId);
                return;
            }

            var gravadas = new List<int> { r.FilmeId };
            await GravarPuloAsync(db, r.FilmeId, validado.InicioA, validado.FimA, AnaliseDeAberturaService.TamanhoDe(caminho));
            for (var i = 0; i < eps.Count; i++)   // o trecho vira MODELO pros outros episódios da temporada
            {
                if (i == pos || impressoes[i].Length == 0) continue;
                if (AprendizadoDePulo.ProcurarModelo(janela, inicioJanela, validado, impressoes[i], minimoPulo) is var (ini, fim)
                    && await GravarPuloAsync(db, eps[i].Id, ini, fim, AnaliseDeAberturaService.TamanhoDe(caminhos[i]))) gravadas.Add(eps[i].Id);
            }
            logger.LogInformation("Abertura aprendida na temporada {T} de {Serie}: {N} episódio(s) marcado(s) ({Ini:F0}s a {Fim:F0}s no episódio pulado).",
                temporada, filme.Serie, gravadas.Count, validado.InicioA, validado.FimA);

            var ligou = await LigarPuloAutomaticoAsync(r.FilmeId, $"Abertura aprendida na temporada {temporada}. Pulo automático ligado.");
            if (!ligou) await AtualizarContextoAsync(r.FilmeId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Aprender com o pulo do filme {Id} falhou.", r.FilmeId);
        }
        finally { _umPorVez.Release(); }
    }

    private static async Task<bool> GravarPuloAsync(AppDbContext db, int filmeId, double inicio, double fim, long bytes)
    {
        var linha = await db.Aberturas.FindAsync(filmeId);
        if (linha is not null && !OrigemMarca.PodeSobrescrever(linha.Origem, OrigemMarca.Pulo)) return false;
        if (linha is null) db.Aberturas.Add(linha = new AberturaEpisodio { FilmeId = filmeId });
        linha.InicioSegundos = Math.Round(inicio, 1);
        linha.FimSegundos = Math.Round(fim, 1);
        linha.Origem = OrigemMarca.Pulo;
        linha.ArquivoBytes = bytes;
        linha.Versao = AnaliseDeAberturaService.VersaoAlgoritmo;
        linha.AnalisadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return true;
    }
}
