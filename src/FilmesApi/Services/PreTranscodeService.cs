using FilmesApi.Data;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// Uma vez por dia, numa hora ociosa, adianta o transcode HLS dos filmes mais prováveis de
/// serem abertos em seguida (retomadas + adições recentes não assistidas). Troca disco por
/// playback instantâneo e espalha o calor pra fora do horário de uso.
///
/// Desligado por padrão (<c>PreTranscodeEnabled</c>). Respeita a fila de 1 job por vez, o
/// governador térmico e nunca começa se já tem alguém assistindo.
/// </summary>
public class PreTranscodeService : BackgroundService
{
    private readonly IServiceScopeFactory _escopos;
    private readonly HlsTranscodeService _transcode;
    private readonly ILogger<PreTranscodeService> _logger;

    private readonly bool _habilitado;
    private readonly int _horaUtc;
    private readonly int _maxItens;
    private DateTime _ultimaPassada = DateTime.MinValue;

    public PreTranscodeService(IServiceScopeFactory escopos, HlsTranscodeService transcode,
        IConfiguration config, ILogger<PreTranscodeService> logger)
    {
        _escopos = escopos;
        _transcode = transcode;
        _logger = logger;
        _habilitado = config.GetValue<bool?>("PreTranscodeEnabled") ?? false;
        _horaUtc = Math.Clamp(config.GetValue<int?>("PreTranscodeHoraUtc") ?? 6, 0, 23);
        _maxItens = Math.Clamp(config.GetValue<int?>("PreTranscodeMaxItens") ?? 5, 1, 50);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_habilitado) return;
        _logger.LogInformation("Pré-transcode noturno ligado: {Hora}h UTC, até {Max} itens por noite.", _horaUtc, _maxItens);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken); }
            catch (OperationCanceledException) { return; }

            var agora = DateTime.UtcNow;
            if (agora.Hour != _horaUtc || _ultimaPassada.Date == agora.Date) continue;
            if (_transcode.TemJobAtivo()) continue;  // tem gente assistindo — deixa pra lá

            _ultimaPassada = agora.Date;
            try { await RodarPassadaAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Pré-transcode: passada noturna falhou."); }
        }
    }

    private async Task RodarPassadaAsync(CancellationToken ct)
    {
        List<(int Id, string Titulo)> alvos;
        using (var escopo = _escopos.CreateScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

            var retomadas = await db.Progressos.AsNoTracking()
                .OrderByDescending(p => p.AtualizadoEm)
                .Select(p => new { p.FilmeId, p.Filme!.Titulo })
                .Take(_maxItens)
                .ToListAsync(ct);

            var recentes = await db.Filmes.AsNoTracking()
                .Where(f => !f.Assistido && f.ArquivoPath != null)
                .OrderByDescending(f => f.DataAdicionado)
                .Select(f => new { FilmeId = f.Id, f.Titulo })
                .Take(_maxItens)
                .ToListAsync(ct);

            alvos = retomadas.Concat(recentes)
                .DistinctBy(x => x.FilmeId)
                .Take(_maxItens)
                .Select(x => (x.FilmeId, x.Titulo))
                .ToList();
        }

        foreach (var (id, titulo) in alvos)
        {
            if (ct.IsCancellationRequested) return;

            string? path;
            using (var escopo = _escopos.CreateScope())
            {
                var filmes = escopo.ServiceProvider.GetRequiredService<FilmeService>();
                var rel = await filmes.ObterArquivoPathAsync(id);
                path = rel is null ? null : filmes.ObterCaminhoAbsoluto(rel);
            }
            if (path is null) continue;

            var (status, _) = await _transcode.ObterStatusAsync(id, path, ct);
            if (status is StreamStatus.Compativel or StreamStatus.Erro) continue;

            _logger.LogInformation("Pré-transcode: preparando {Titulo} (#{Id})…", titulo, id);
            // Segura o job vivo (senão o abortador de órfão o mata em 90s por falta de acesso)
            // e serializa: espera este sair da fila antes de chamar o próximo.
            while (!ct.IsCancellationRequested && _transcode.TemJobDoFilme(id))
            {
                _transcode.RegistrarInteresse(id);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        _logger.LogInformation("Pré-transcode: passada noturna concluída ({N} alvos).", alvos.Count);
    }
}
