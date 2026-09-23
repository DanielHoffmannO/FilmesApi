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

    /// <summary>O que fazer com um relato de posição — <c>Descartar*</c> quando é cedo demais
    /// pra valer a retomada ou perto demais do fim; <c>Inserir</c>/<c>Atualizar</c> senão.
    /// Extraída como decisão pura (sem tocar o banco) pra dar pra testar a regra de negócio
    /// sem precisar de EF Core/SQLite no teste — mesmo padrão de
    /// <see cref="HlsTranscodeService.ClassificarCompatibilidade"/>.</summary>
    internal enum DecisaoSalvarProgresso { Descartar, DescartarEMarcarAssistido, Inserir, Atualizar }

    internal static DecisaoSalvarProgresso DecidirSalvamento(double posicao, double? duracao, bool jaTemProgresso)
    {
        var pertoDoFim = duracao is > 0 && posicao >= duracao.Value - MargemFim(duracao.Value);
        if (posicao < MinSegundosParaSalvar || pertoDoFim)
            return pertoDoFim ? DecisaoSalvarProgresso.DescartarEMarcarAssistido : DecisaoSalvarProgresso.Descartar;
        return jaTemProgresso ? DecisaoSalvarProgresso.Atualizar : DecisaoSalvarProgresso.Inserir;
    }

    private readonly AppDbContext _db;

    public ProgressoService(AppDbContext db) => _db = db;

    /// <summary>Upsert do progresso. Retorna false se o filme não existe.</summary>
    public async Task<bool> SalvarAsync(int filmeId, double posicao, double? duracao)
    {
        var filme = await _db.Filmes.Include(f => f.Progresso).FirstOrDefaultAsync(f => f.Id == filmeId);
        if (filme is null) return false;

        posicao = Math.Max(0, posicao);
        var decisao = DecidirSalvamento(posicao, duracao, filme.Progresso is not null);

        switch (decisao)
        {
            case DecisaoSalvarProgresso.Descartar:
            case DecisaoSalvarProgresso.DescartarEMarcarAssistido:
                // Começo do filme ou praticamente no fim: não guarda ponto de retomada.
                if (filme.Progresso is not null) _db.Progressos.Remove(filme.Progresso);
                if (decisao == DecisaoSalvarProgresso.DescartarEMarcarAssistido) filme.Assistido = true;
                break;
            case DecisaoSalvarProgresso.Inserir:
                _db.Progressos.Add(new ProgressoReproducao
                {
                    FilmeId = filmeId,
                    PosicaoSegundos = posicao,
                    DuracaoSegundos = duracao,
                    AtualizadoEm = DateTime.UtcNow,
                });
                break;
            case DecisaoSalvarProgresso.Atualizar:
                filme.Progresso!.PosicaoSegundos = posicao;
                filme.Progresso.DuracaoSegundos = duracao ?? filme.Progresso.DuracaoSegundos;
                filme.Progresso.AtualizadoEm = DateTime.UtcNow;
                break;
        }

        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Corrida com outro save/concluir do mesmo filme (aba dupla, pagehide + pause).
            // O próximo tick de progresso corrige — melhor engolir do que 500 no player.
            _db.ChangeTracker.Clear();
        }
        return true;
    }

    public async Task<ProgressoResponse?> ObterAsync(int filmeId)
    {
        var p = await _db.Progressos.AsNoTracking().FirstOrDefaultAsync(x => x.FilmeId == filmeId);
        return p is null ? null : new ProgressoResponse(p.FilmeId, p.PosicaoSegundos, p.DuracaoSegundos, p.AtualizadoEm);
    }


    /// <summary>Reprodução chegou ao fim: marca o filme como assistido e limpa a retomada.
    /// Usado pelo evento 'ended' do player — inclusive no HLS, onde a regra de "perto do fim"
    /// não roda porque a duração não é confiável durante o transcode.</summary>
    public async Task<bool> ConcluirAsync(int filmeId)
    {
        if (!await _db.Filmes.AnyAsync(f => f.Id == filmeId)) return false;

        // Bulk update/delete (sem change tracker): o 'ended' costuma disparar junto com um
        // salvarProgresso, e dois /concluir concorrentes eram DbUpdateConcurrencyException
        // (linha de progresso já apagada). Assim cada operação é um WHERE idempotente —
        // apagar 0 linhas é ok.
        await _db.Filmes.Where(f => f.Id == filmeId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Assistido, true));
        await _db.Progressos.Where(p => p.FilmeId == filmeId).ExecuteDeleteAsync();
        return true;
    }

    /// <summary>Filmes com reprodução pendente, do mais recente pro mais antigo.</summary>
    public async Task<List<ContinuarAssistindoResponse>> ContinuarAssistindoAsync(int limite = 20)
    {
        // Toda linha em Progressos já tem PosicaoSegundos >= MinSegundosParaSalvar (SalvarAsync garante).
        //
        // !Filme.Assistido: Progresso e Assistido=true não deveriam coexistir pro mesmo filme
        // (ConcluirAsync sempre apaga o Progresso ao marcar assistido, na mesma operação) --
        // mas um progresso tardio de uma sessão abandonada pode recriar a linha depois. Caso
        // real: assiste um episódio no celular, pausa sem fechar o player, termina o MESMO
        // episódio na TV (marca assistido, apaga o progresso) -- se o celular (aba ainda aberta
        // em segundo plano) mandar mais um salvarProgresso() depois disso (típico no
        // 'pagehide', ao finalmente fechar a aba), isso reinsere um Progresso com a posição
        // VELHA do celular, e o episódio (já visto de verdade) reaparecia aqui como se não
        // tivesse terminado. Filtrar aqui é seguro: em operação normal nunca exclui nada que
        // não fosse já esperado sumir.
        return await _db.Progressos.AsNoTracking()
            .Where(p => !p.Filme!.Assistido)
            .OrderByDescending(p => p.AtualizadoEm)
            .Take(limite)
            .Select(p => new ContinuarAssistindoResponse(
                p.FilmeId, p.Filme!.Titulo,
                p.PosicaoSegundos, p.DuracaoSegundos, p.AtualizadoEm))
            .ToListAsync();
    }
}
