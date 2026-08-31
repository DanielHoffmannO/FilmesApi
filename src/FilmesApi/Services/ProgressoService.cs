using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>
/// Guarda e recupera o ponto de retomada de cada filme ("continuar de onde parou").
/// </summary>
public class ProgressoService
{
    /// <summary>Abaixo disso não vale a pena retomar — trata como "começou agora".</summary>
    private const double MinSegundosParaSalvar = 15;

    /// <summary>
    /// Se parou a esta distância (ou menos) do fim, considera assistido e não guarda retomada.
    /// 90s para um filme normal, mas encolhe para 10% da duração em vídeos curtos (clipes,
    /// trailers) — senão qualquer posição contaria como "no fim".
    /// </summary>
    private static double MargemFim(double duracao) => Math.Min(90, duracao * 0.1);

    private readonly AppDbContext _db;

    public ProgressoService(AppDbContext db) => _db = db;

    /// <summary>Upsert do progresso. Retorna false se o filme não existe.</summary>
    public async Task<bool> SalvarAsync(int filmeId, double posicao, double? duracao)
    {
        var filme = await _db.Filmes.Include(f => f.Progresso).FirstOrDefaultAsync(f => f.Id == filmeId);
        if (filme is null) return false;

        posicao = Math.Max(0, posicao);
        var pertoDoFim = duracao is > 0 && posicao >= duracao.Value - MargemFim(duracao.Value);

        if (posicao < MinSegundosParaSalvar || pertoDoFim)
        {
            // Começo do filme ou praticamente no fim: não guarda ponto de retomada.
            if (filme.Progresso is not null) _db.Progressos.Remove(filme.Progresso);
            if (pertoDoFim) filme.Assistido = true;
            await _db.SaveChangesAsync();
            return true;
        }

        if (filme.Progresso is null)
        {
            _db.Progressos.Add(new ProgressoReproducao
            {
                FilmeId = filmeId,
                PosicaoSegundos = posicao,
                DuracaoSegundos = duracao,
                AtualizadoEm = DateTime.UtcNow,
            });
        }
        else
        {
            filme.Progresso.PosicaoSegundos = posicao;
            filme.Progresso.DuracaoSegundos = duracao ?? filme.Progresso.DuracaoSegundos;
            filme.Progresso.AtualizadoEm = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<ProgressoResponse?> ObterAsync(int filmeId)
    {
        var p = await _db.Progressos.AsNoTracking().FirstOrDefaultAsync(x => x.FilmeId == filmeId);
        return p is null ? null : new ProgressoResponse(p.FilmeId, p.PosicaoSegundos, p.DuracaoSegundos, p.AtualizadoEm);
    }

    /// <summary>Esquece o ponto de retomada ("assistir do começo"). False se não havia nada guardado.</summary>
    public async Task<bool> LimparAsync(int filmeId)
    {
        var removidos = await _db.Progressos.Where(p => p.FilmeId == filmeId).ExecuteDeleteAsync();
        return removidos > 0;
    }

    /// <summary>Filmes com reprodução pendente, do mais recente pro mais antigo.</summary>
    public async Task<List<ContinuarAssistindoResponse>> ContinuarAssistindoAsync(int limite = 20)
    {
        return await _db.Progressos.AsNoTracking()
            .Where(p => p.PosicaoSegundos >= MinSegundosParaSalvar)
            .OrderByDescending(p => p.AtualizadoEm)
            .Take(limite)
            .Select(p => new ContinuarAssistindoResponse(
                p.FilmeId, p.Filme!.Titulo, p.Filme.ArquivoPath,
                p.PosicaoSegundos, p.DuracaoSegundos, p.AtualizadoEm))
            .ToListAsync();
    }
}
