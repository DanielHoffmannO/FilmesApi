using System.Collections.Concurrent;
using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// Em segundo plano, descobre quais arquivos têm uma faixa de "dublagem por IA" (<see cref="EscolhaAudio.EhDubladoIa(FaixaAudio?)"/>) e guarda o
/// resultado no banco (<see cref="AnaliseDeDublagem"/>) — é de lá que o catálogo tira o <c>dubladoIa</c> de cada item. Listar o catálogo NUNCA
/// roda ffprobe (a placa é fraca e o acervo tem ~1100 itens): a leitura das faixas é paga aqui, uma vez por arquivo, aos poucos.
/// <list type="bullet">
///   <item><b>Backfill gradual</b>: nenhum trabalho no boot; depois de <c>DubladoIaAtrasoInicialSegundos</c> (90) cada rodada analisa até
///     <c>DubladoIaLoteMax</c> (60) arquivos ou <c>DubladoIaOrcamentoSegundos</c> (60), o que vier primeiro, só com o servidor ocioso
///     (<see cref="HlsTranscodeService.Ocioso"/>); com fila sobrando a próxima rodada vem em <c>DubladoIaPausaEntreLotesSegundos</c> (30).</item>
///   <item><b>Em dia</b>: a cada <c>DubladoIaIntervaloMinutos</c> (10; 0 desliga) confere o tamanho de cada arquivo (um stat, sem ler o disco). Tamanho
///     diferente do guardado = arquivo trocado (o <c>scripts/dublar</c> acrescentou a faixa) — volta à fila antes dos nunca analisados.</item>
///   <item>O ffprobe é o do <see cref="MediaProbeService"/> (cacheado): quem abrir o episódio depois não paga de novo.</item>
///   <item>A mesma leitura das faixas guarda também se há áudio em português que NÃO é IA (<see cref="AnaliseDeDublagem.AudioPtNaoIa"/>, o
///     <c>dublado</c> do catálogo — ver <see cref="DublagemEmPortugues"/>). Linhas de antes dessa coluna (NULL) voltam à fila depois dos nunca
///     analisados; enquanto isso o <c>dublado</c> delas vem da dica no nome do arquivo.</item>
/// </list>
/// Falha de ffprobe (arquivo ainda sendo escrito, disco lento) não grava nada e só é repetida depois de <see cref="EsperaAposFalha"/>.
/// </summary>
public class DubladoIaService(IServiceScopeFactory escopos, IConfiguration config, ILogger<DubladoIaService> logger,
    RegistroDeTarefas? tarefas = null) : BackgroundService
{
    internal const string NomeDaTarefa = "dublado-ia";
    internal static readonly TimeSpan EsperaAposFalha = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<int, DateTime> _falhas = new();
    private int _pendentes;
    private volatile bool _servidorOcupado;

    /// <summary>Quantos arquivos ainda esperam análise (depois da última rodada). Só pros testes e pro ritmo das rodadas.</summary>
    internal int Pendentes => Volatile.Read(ref _pendentes);

    private sealed record Item(int Id, string Caminho, long Bytes);

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var minutos = config.GetValue<double?>("DubladoIaIntervaloMinutos") ?? 10;
        if (minutos <= 0) { tarefas?.Declarar(NomeDaTarefa, ligada: false); return; }
        tarefas?.Declarar(NomeDaTarefa);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue<double?>("DubladoIaAtrasoInicialSegundos") ?? 90), parar);
            while (true)
            {
                await ExecutarUmaVezAsync(parar);
                // Fila sobrando (e o servidor livre): a próxima rodada vem logo, pro backfill levar minutos e não horas.
                var curta = Pendentes > 0 && !_servidorOcupado;
                await Task.Delay(curta
                    ? TimeSpan.FromSeconds(config.GetValue<double?>("DubladoIaPausaEntreLotesSegundos") ?? 30)
                    : TimeSpan.FromMinutes(minutos), parar);
            }
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Marca <c>DubladoIa</c> e <c>Dublado</c> nos itens da lista a partir do que está guardado no banco — uma consulta barata, sem tocar
    /// em disco. <c>Dublado</c> = a análise por probe, se já existe e é conclusiva; senão a dica no nome do arquivo
    /// (<see cref="DublagemEmPortugues.Resolver"/>). Chamado por <see cref="FilmeService.ListarAsync"/>.</summary>
    internal static async Task MarcarAsync(AppDbContext db, List<FilmeResponse> lista)
    {
        if (lista.Count == 0) return;
        var linhas = (await db.AnalisesDeDublagem.AsNoTracking().Select(a => new { a.FilmeId, a.DubladoIa, a.AudioPtNaoIa }).ToListAsync())
            .ToDictionary(a => a.FilmeId);
        for (var i = 0; i < lista.Count; i++)
        {
            linhas.TryGetValue(lista[i].Id, out var linha);
            var ia = linha?.DubladoIa ?? false;
            var dublado = DublagemEmPortugues.Resolver(linha?.AudioPtNaoIa, lista[i].ArquivoPath);
            if (ia || dublado) lista[i] = lista[i] with { DubladoIa = ia, Dublado = dublado };
        }
    }

    /// <summary>Uma rodada. Nunca lança (uma rodada que falha não derruba o timer nem o app). Devolve quantos arquivos foram analisados.</summary>
    internal async Task<int> ExecutarUmaVezAsync(CancellationToken ct = default)
    {
        var medicao = tarefas?.Iniciar(NomeDaTarefa);
        int analisados = 0, falhas = 0;
        string? pausa = null;
        try
        {
            using var escopo = escopos.CreateScope();
            var sp = escopo.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var transcode = sp.GetRequiredService<HlsTranscodeService>();
            var filmes = sp.GetRequiredService<FilmeService>();
            var probe = sp.GetRequiredService<MediaProbeService>();

            var fila = await PendentesAsync(db, config.GetValue<string>("MediaPath") ?? "/media", ct);
            var limite = config.GetValue<int?>("DubladoIaLoteMax") ?? 60;
            var fim = DateTime.UtcNow.AddSeconds(config.GetValue<double?>("DubladoIaOrcamentoSegundos") ?? 60);
            _servidorOcupado = false;

            foreach (var item in fila)
            {
                if (analisados >= limite) { pausa = "lote cheio"; break; }
                if (!transcode.Ocioso) { _servidorOcupado = true; pausa = "servidor ocupado (transcode/remux ativo)"; break; }
                if (DateTime.UtcNow > fim) { pausa = "orçamento da rodada esgotado"; break; }
                ct.ThrowIfCancellationRequested();

                // Sem caminho (sumiu entre a conferência e agora), sem ffprobe ou sem como gravar: não repete já na próxima rodada.
                var info = await filmes.ResolverCaminhoAsync(item.Id) is { } caminho ? await ProbeSeguroAsync(probe, caminho, ct) : null;
                if (info is not null && await GravarAsync(db, item, info.Audios.Any(EscolhaAudio.EhDubladoIa), DublagemEmPortugues.ClassificarAudios(info.Audios), ct)) analisados++;
                else { _falhas[item.Id] = DateTime.UtcNow; falhas++; }
            }

            Volatile.Write(ref _pendentes, Math.Max(0, fila.Count - analisados - falhas));
            var comIa = await db.AnalisesDeDublagem.AsNoTracking().CountAsync(a => a.DubladoIa, ct);
            medicao?.Ok(Resumo(analisados, Pendentes, comIa, pausa));
            if (analisados > 0) logger.LogInformation("Dublagem IA: {N} arquivo(s) analisado(s), {Pendentes} pendente(s), {Com} com faixa IA.", analisados, Pendentes, comIa);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            medicao?.Falhou(ex);
            logger.LogWarning(ex, "Análise de dublagem por IA falhou — tenta de novo na próxima rodada.");
        }
        return analisados;
    }

    /// <summary>A linha do card "Tarefas" do /api/status.</summary>
    internal static string Resumo(int analisados, int pendentes, int comIa, string? pausa)
    {
        var texto = $"{comIa} com dublagem IA · {pendentes} pendente(s)";
        if (analisados > 0) texto = $"{analisados} analisado(s) agora · " + texto;
        return pausa is null ? texto : texto + $" · pausado: {pausa}";
    }

    /// <summary>Quem precisa de análise: sem linha (nunca visto), com tamanho diferente do guardado (arquivo trocado) ou com a linha de antes da coluna
    /// <c>AudioPtNaoIa</c> (nunca olhado pro português). Os trocados vão primeiro — é o que o dono acabou de dublar —, depois os nunca vistos, depois
    /// as linhas antigas, cada grupo do mais novo pro mais antigo. Arquivo que não dá pra medir (sumiu, disco caído) e o que falhou há pouco ficam de fora.</summary>
    private async Task<List<Item>> PendentesAsync(AppDbContext db, string pastaDeMidia, CancellationToken ct)
    {
        var todos = await db.Filmes.AsNoTracking().Where(f => f.ArquivoPath != null).Select(f => new { f.Id, Caminho = f.ArquivoPath! }).ToListAsync(ct);
        var feitos = await db.AnalisesDeDublagem.AsNoTracking().ToDictionaryAsync(a => a.FilmeId, a => (a.ArquivoBytes, SemPt: a.AudioPtNaoIa == null), ct);
        var agora = DateTime.UtcNow;

        List<Item> trocados = [], novos = [], antigos = [];
        foreach (var f in todos)
        {
            if (MediaNomeParser.Ignorado(f.Caminho)) continue;
            if (_falhas.TryGetValue(f.Id, out var quando) && agora - quando < EsperaAposFalha) continue;
            var bytes = AnaliseDeAberturaService.TamanhoDe(Path.Combine(pastaDeMidia, f.Caminho));
            if (bytes <= 0) continue;
            if (!feitos.TryGetValue(f.Id, out var guardado)) novos.Add(new Item(f.Id, f.Caminho, bytes));
            else if (guardado.ArquivoBytes != bytes) trocados.Add(new Item(f.Id, f.Caminho, bytes));
            else if (guardado.SemPt) antigos.Add(new Item(f.Id, f.Caminho, bytes));
        }
        return [.. trocados, .. novos.OrderByDescending(n => n.Id), .. antigos.OrderByDescending(n => n.Id)];
    }

    /// <summary>Cria/atualiza a linha do arquivo. false se não deu pra gravar (o filme sumiu do banco no meio — o scan apagou o órfão).</summary>
    private async Task<bool> GravarAsync(AppDbContext db, Item item, bool dubladoIa, AudioPortugues audioPt, CancellationToken ct)
    {
        try
        {
            var linha = await db.AnalisesDeDublagem.FindAsync([item.Id], ct);
            var eraIa = linha?.DubladoIa ?? false;
            if (linha is null) db.AnalisesDeDublagem.Add(linha = new AnaliseDeDublagem { FilmeId = item.Id });
            linha.DubladoIa = dubladoIa;
            linha.AudioPtNaoIa = audioPt;
            linha.ArquivoBytes = item.Bytes;
            linha.AnalisadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (dubladoIa && !eraIa) logger.LogInformation("Dublagem por IA encontrada em '{Arquivo}'.", item.Caminho);
            return true;
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            logger.LogDebug(ex, "Não gravei a análise de dublagem do filme {Id} (sumiu do catálogo?).", item.Id);
            return false;
        }
    }

    private static async Task<MediaInfo?> ProbeSeguroAsync(MediaProbeService probe, string caminho, CancellationToken ct)
    {
        try { return await probe.InspecionarAsync(caminho, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }   // ffprobe ausente/quebrado: tenta depois
    }
}
