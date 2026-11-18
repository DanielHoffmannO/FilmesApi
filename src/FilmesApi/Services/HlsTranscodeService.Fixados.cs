namespace FilmesApi.Services;

public partial class HlsTranscodeService
{
    /// <summary>Tem job de remux rodando pra este filme? (o <see cref="CacheFixadoService"/> espera um terminar antes de começar o próximo.)</summary>
    internal bool RemuxEmAndamento(int filmeId) => _remuxJobs.ContainsKey(filmeId);

    /// <summary>Tamanho do remux pronto deste filme, ou null se não existe.</summary>
    internal long? BytesDoRemux(int filmeId)
    {
        try { var fi = new FileInfo(CaminhoRemux(filmeId)); return fi.Exists ? fi.Length : null; }
        catch (IOException) { return null; }
    }

    /// <summary>O remux de <paramref name="tamanho"/> bytes cabe no teto SEM expulsar nada? (a conta do pré-aquecimento e do cache fixado.)</summary>
    internal bool CabeSemExpulsar(long tamanho)
    {
        // o que o despejo por teto tiraria sem dó (episódio já visto, não fixado e parado há mais de 30 min) conta como espaço livre
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
        var (entradas, _) = LimpezaDeCache.Listar(PastaHls, PastaRemux, ganchos.UltimoAcesso);   // sobras (.tmp/.audio soltos) ficam pra limpeza automática
        var itens = 0;
        long bytes = 0;
        foreach (var e in entradas)
        {
            if (fixados.Contains(e.Id) || e.Acesso > agora - OpcoesLimpeza.MargemDeUso || ganchos.TemJob(e.Id, e.Tipo)) continue;
            ganchos.Apagar(e.Id, e.Tipo);
            itens++; bytes += e.Bytes;
        }
        _logger.LogInformation("Limpar cache (pedido pelo dono): {Itens} itens, {Gb:F1} GB; {Fix} fixados preservados.", itens, bytes / 1073741824.0, fixados.Count);
        return new ResumoLimpeza(itens, bytes);
    }
}
