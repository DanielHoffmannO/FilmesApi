namespace FilmesApi.Services;

public partial class HlsTranscodeService
{
    /// <summary>Tem job de remux rodando pra este filme? (o <see cref="CacheFixadoService"/> espera um terminar antes de começar o próximo.)</summary>
    internal bool RemuxEmAndamento(int filmeId) => _remuxJobs.ContainsKey(filmeId);

    /// <summary>Tamanho do remux pronto deste filme, ou null se não existe.</summary>
    internal long? BytesDoRemux(int filmeId)
    {
        try { return File.Exists(CaminhoRemux(filmeId)) ? new FileInfo(CaminhoRemux(filmeId)).Length : null; }
        catch (IOException) { return null; }
    }

    /// <summary>O remux de <paramref name="tamanho"/> bytes cabe no teto SEM expulsar nada? (mesma conta do <see cref="PreAquecerRemux"/>, sem logar.)</summary>
    internal bool CabeSemExpulsar(long tamanho)
    {
        var noCache = Math.Max(0, MedirCacheRemux().Bytes - BytesDeRemuxAssistidoDespejavel());
        return PoliticaPreAquecimento.CabeNoCache(noCache, tamanho, _remuxCacheMaxBytes);
    }

    /// <summary>Bytes que o remux ocupa no total (pro retrato do cache).</summary>
    internal long BytesTotaisDoRemux() => MedirCacheRemux().Bytes;

    /// <summary>"Limpar cache": apaga tudo que NÃO está fixado, sem job e sem uso recente (<see cref="OpcoesLimpeza.MargemDeUso"/>).
    /// Só mexe em filhos diretos das pastas de cache, como a limpeza automática.</summary>
    internal ResumoLimpeza LimparNaoFixados(IReadOnlySet<int> fixados)
    {
        var agora = DateTime.UtcNow;
        var ganchos = GanchosDeCache();
        var (entradas, sobras) = LimpezaDeCache.Listar(PastaHls, PastaRemux, ganchos.UltimoAcesso);
        var itens = 0;
        long bytes = 0;
        foreach (var e in entradas)
        {
            if (fixados.Contains(e.Id) || e.Acesso > agora - OpcoesLimpeza.MargemDeUso || ganchos.TemJob(e.Id, e.Tipo)) continue;
            ganchos.Apagar(e.Id, e.Tipo);
            itens++; bytes += e.Bytes;
        }
        _ = sobras;   // sobras (.tmp/.audio soltos) ficam pra limpeza automática
        _logger.LogInformation("Limpar cache (pedido pelo dono): {Itens} itens, {Gb:F1} GB; {Fix} fixados preservados.", itens, bytes / 1073741824.0, fixados.Count);
        return new ResumoLimpeza(itens, bytes);
    }
}
