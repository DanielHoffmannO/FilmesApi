using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>As decisões puras do "Manter no cache" (testáveis sem disco).</summary>
internal static class PoliticaCacheFixado
{
    /// <summary>A cópia nova cabe no teto de GB reservado aos fixados? Teto ilimitado (<c>long.MaxValue</c>) sempre cabe.</summary>
    public static bool CabeNoTetoFixado(long fixadoProntoBytes, long tamanho, long tetoFixadoBytes) =>
        tetoFixadoBytes == long.MaxValue || fixadoProntoBytes + tamanho <= tetoFixadoBytes;

    /// <summary>Limite de escrita por rodada: depois de gravar isto, a rodada para e o resto espera a próxima (0 = sem limite).</summary>
    public static bool AtingiuLimiteDaRodada(long escritoBytes, long limiteBytes) => limiteBytes > 0 && escritoBytes >= limiteBytes;
}

/// <summary>
/// "Manter no cache": guarda a lista dos títulos fixados (tabela <c>CacheFixado</c>) e prepara, aos poucos, uma cópia .mp4 de cada um
/// no cache do NVMe (<c>remux/{id}.mp4</c>, cópia de vídeo/áudio sem recodificar quando possível — ~40 s por episódio de 1 GB). O player passa a
/// preferir essa cópia, então tocar não depende do SSD USB de mídia. Regras de segurança: nunca expulsa fixado (ver <see cref="HlsTranscodeService.ObterFixados"/>);
/// só prepara com o servidor ocioso e se cabe no teto do cache SEM expulsar nada e no teto dos fixados (<c>CacheFixadoMaxGB</c>, 40); no máximo
/// <c>CacheFixadoGbPorRodada</c> (5) GB gravados por rodada, uma cópia de cada vez; <c>CacheFixadoIntervaloSegundos</c> (60; 0 desliga a preparação).
/// </summary>
public class CacheFixadoService(IServiceScopeFactory escopos, HlsTranscodeService transcode, IConfiguration config,
    ILogger<CacheFixadoService> logger) : BackgroundService
{
    private volatile IReadOnlySet<int> _fixados = new HashSet<int>();
    private volatile IReadOnlyList<int> _ordem = [];
    private readonly SemaphoreSlim _acorda = new(0);
    private readonly HashSet<int> _avisados = [];   // já logou "adiado"/"arquivo fora do ar" desta vez — não repete a cada rodada

    public IReadOnlySet<int> Fixados => _fixados;
    public IReadOnlyList<int> Ordem => _ordem;

    public long TetoFixadoBytes => ParaBytes(config.GetValue<double?>("CacheFixadoMaxGB") ?? 40);
    private long LimiteDaRodadaBytes => ParaBytes(config.GetValue<double?>("CacheFixadoGbPorRodada") ?? 5, semLimiteQuandoZero: false);

    private static long ParaBytes(double gb, bool semLimiteQuandoZero = true) =>
        gb > 0 ? (long)(gb * 1024 * 1024 * 1024) : (semLimiteQuandoZero ? long.MaxValue : 0);

    public async Task CarregarAsync()
    {
        using var escopo = escopos.CreateScope();
        var linhas = await escopo.ServiceProvider.GetRequiredService<AppDbContext>().CacheFixados.AsNoTracking()
            .OrderBy(c => c.FixadoEm).Select(c => c.FilmeId).ToListAsync();
        _ordem = linhas;
        _fixados = linhas.ToHashSet();
    }

    /// <summary>Fixa os que existem no catálogo e ainda não estavam fixados; devolve quantos entraram. Acorda a preparação.</summary>
    public async Task<int> FixarAsync(IEnumerable<int> ids)
    {
        using var escopo = escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var pedidos = ids.Distinct().ToList();
        var existentes = await db.Filmes.AsNoTracking().Where(f => pedidos.Contains(f.Id)).Select(f => f.Id).ToListAsync();
        var jaFixados = await db.CacheFixados.AsNoTracking().Select(c => c.FilmeId).ToListAsync();
        var novos = existentes.Except(jaFixados).ToList();
        foreach (var id in novos) db.CacheFixados.Add(new CacheFixado { FilmeId = id, FixadoEm = DateTime.UtcNow });
        if (novos.Count > 0) await db.SaveChangesAsync();
        await CarregarAsync();
        if (novos.Count > 0)
        {
            logger.LogInformation("Cache fixado: {Novos} título(s) marcados para manter ({Total} no total).", novos.Count, _fixados.Count);
            _acorda.Release();
        }
        return novos.Count;
    }

    /// <summary>Tira da lista de fixados (a cópia fica no cache como qualquer outra e sai pelas regras normais).</summary>
    public async Task<int> DesafixarAsync(IEnumerable<int> ids)
    {
        using var escopo = escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var pedidos = ids.Distinct().ToList();
        var remover = await db.CacheFixados.Where(c => pedidos.Contains(c.FilmeId)).ToListAsync();
        if (remover.Count > 0)
        {
            db.CacheFixados.RemoveRange(remover);
            await db.SaveChangesAsync();
            logger.LogInformation("Cache fixado: {N} título(s) liberados.", remover.Count);
        }
        await CarregarAsync();
        return remover.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var segundos = config.GetValue<double?>("CacheFixadoIntervaloSegundos") ?? 60;
        if (segundos <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue<double?>("CacheFixadoAtrasoInicialSegundos") ?? 90), parar);
            while (!parar.IsCancellationRequested)
            {
                try { await RodadaAsync(parar); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Rodada do cache fixado falhou — tenta de novo na próxima."); }
                await _acorda.WaitAsync(TimeSpan.FromSeconds(segundos), parar);
            }
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Uma rodada: percorre os fixados na ordem em que foram marcados e prepara a cópia dos que faltam, um de cada vez.</summary>
    internal async Task RodadaAsync(CancellationToken ct)
    {
        var ordem = _ordem;
        if (ordem.Count == 0) return;
        var teto = TetoFixadoBytes;
        var limite = LimiteDaRodadaBytes;
        var prontos = ordem.Sum(id => transcode.BytesDoRemux(id) ?? 0);
        long escrito = 0;

        foreach (var id in ordem)
        {
            ct.ThrowIfCancellationRequested();
            if (transcode.BytesDoRemux(id) is not null) { _avisados.Remove(id); continue; }
            if (PoliticaCacheFixado.AtingiuLimiteDaRodada(escrito, limite))
            {
                logger.LogInformation("Cache fixado: limite de escrita da rodada ({Gb:F1} GB) atingido; o resto fica para a próxima.", limite / 1073741824.0);
                return;
            }

            string? origem;
            using (var escopo = escopos.CreateScope())
                origem = await escopo.ServiceProvider.GetRequiredService<FilmeService>().ResolverCaminhoAsync(id);
            long tamanho;
            try { tamanho = origem is null ? -1 : new FileInfo(origem).Length; }
            catch (IOException) { tamanho = -1; }
            if (tamanho <= 0)
            {
                if (_avisados.Add(id)) logger.LogWarning("Cache fixado: o arquivo do título {Id} não está acessível (SSD de mídia fora do ar?); tento de novo nas próximas rodadas.", id);
                continue;
            }
            if (!PoliticaCacheFixado.CabeNoTetoFixado(prontos, tamanho, teto) || !transcode.CabeSemExpulsar(tamanho))
            {
                if (_avisados.Add(id)) logger.LogInformation("Cache fixado: título {Id} ({Mb:F0} MB) adiado — não cabe no teto sem expulsar nada.", id, tamanho / 1048576.0);
                continue;
            }
            if (!transcode.Ocioso) return;   // servidor ocupado (alguém assistindo/convertendo, ou placa quente): não atrapalha, tenta depois
            if (!transcode.PreAquecerRemux(id, origem!)) continue;

            _avisados.Remove(id);
            while (transcode.RemuxEmAndamento(id)) await Task.Delay(2000, ct);
            if (transcode.BytesDoRemux(id) is long gravado)
            {
                escrito += gravado; prontos += gravado;
                logger.LogInformation("Cache fixado: título {Id} pronto ({Mb:F0} MB).", id, gravado / 1048576.0);
            }
            else
            {
                logger.LogInformation("Cache fixado: a cópia do título {Id} não terminou (interrompida ou falhou); tento de novo na próxima rodada.", id);
                return;
            }
        }
    }
}
