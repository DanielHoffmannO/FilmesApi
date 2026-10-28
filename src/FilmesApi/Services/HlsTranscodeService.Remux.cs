using System.Diagnostics;

namespace FilmesApi.Services;

public partial class HlsTranscodeService
{
    public string CaminhoRemux(int filmeId) => Path.Combine(_remuxCachePath, $"{filmeId}{NomesDeCache.ExtensaoRemux}");

    /// <summary>Caminho do .mp4 remuxado se já está completo em disco (e marca o acesso pro
    /// LRU), senão null.</summary>
    public string? ObterRemuxPronto(int filmeId)
    {
        var caminho = CaminhoRemux(filmeId);
        if (!File.Exists(caminho)) return null;
        RegistrarAcesso(caminho);  // toca o mtime = sinal de LRU; /remux não passa mais por ObterStatusRemux depois de "disponivel"
        return caminho;
    }

    /// <summary>Estado do remux (cache em disco, um .mp4 por filme): dispara o job na primeira
    /// chamada, reusa nas seguintes. Ao contrário do /remux antigo (pipe ao vivo, sem
    /// Content-Length — algumas TVs recusam isso na hora, sem nem tentar), aqui o arquivo só
    /// fica "disponivel" quando está completo e gravado, servido depois com Range normal
    /// (<see cref="Controllers.ReproducaoController.ServirComRange"/>) — qualquer player aceita.
    /// Enquanto "preparando", devolve também um progresso estimado (tamanho do .tmp / tamanho
    /// do original — o vídeo é copiado quase 1:1, só o áudio encolhe, então isso sobra perto
    /// do fim; por isso o teto de 99% até realmente terminar). É só pra não parecer travado
    /// num filme de 2h+ — não precisa ser exato.</summary>
    public (RemuxStatus Status, int? ProgressoPercent) ObterStatusRemux(int filmeId, string origem, bool preAquecimento = false)
    {
        lock (_remuxLock)
        {
            // um pedido de verdade assume o job: dali em diante vale a regra normal (órfão aborta, não cede lugar)
            if (!preAquecimento) _remuxQuente.TryRemove(filmeId, out _);
            if (_remuxFalhas.TryGetValue(filmeId, out var falhouEm))
            {
                if (DateTime.UtcNow - falhouEm < _falhaCooldown) return (RemuxStatus.Erro, null);
                _remuxFalhas.TryRemove(filmeId, out _);
            }
            if (File.Exists(CaminhoRemux(filmeId))) return (RemuxStatus.Disponivel, 100);
            if (!_remuxJobs.ContainsKey(filmeId))
            {
                if (preAquecimento) _remuxQuente[filmeId] = 0;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_pararToken);
                _jobCancelRemux[filmeId] = cts;
                _remuxJobs[filmeId] = Task.Run(() => ExecutarRemuxAsync(filmeId, origem, cts.Token));
            }
            _ultimoAcesso[filmeId] = DateTime.UtcNow;  // disparo e cada poll contam como interesse (ver ExecutarRemuxAsync)
            return (RemuxStatus.Preparando, EstimarProgressoRemux(filmeId, origem));
        }
    }

    private int? EstimarProgressoRemux(int filmeId, string origem)
    {
        try
        {
            var tmp = CaminhoRemux(filmeId) + NomesDeCache.SufixoTmp;
            if (!File.Exists(tmp)) return 0;
            var origBytes = new FileInfo(origem).Length;
            if (origBytes <= 0) return null;
            var pct = (int)(100.0 * new FileInfo(tmp).Length / origBytes);
            return Math.Clamp(pct, 0, 99);
        }
        catch (IOException) { return null; }
    }

    private async Task ExecutarRemuxAsync(int filmeId, string origem, CancellationToken ct)
    {
        var final = CaminhoRemux(filmeId);
        var tmp = final + NomesDeCache.SufixoTmp;
        var slotObtido = false;
        var orfao = false;
        try
        {
            // Mesmo gate de temperatura/concorrência do HLS: mesmo o remux sendo leve
            // (video só copia, só o áudio processa), ele ainda compete pela mesma CPU/placa —
            // sem isso um remux e um reencode HLS rodando juntos já esquentaram a placa hoje.
            await _thermal.AguardarResfriamentoAsync(ct);
            await EsperarSlotAsync(ct);
            slotObtido = true;
            await _thermal.AguardarResfriamentoAsync(ct);

            // Ficou na fila (slot/temperatura) e quem pediu já foi embora: nem começa. (Pré-aquecimento não tem quem
            // peça: ele mesmo confere de novo se ainda vale a pena começar.)
            if (DeveAbortarRemux(filmeId)) { RemuxOrfao(filmeId); return; }

            var info = await _probe.InspecionarAsync(origem, CancellationToken.None)
                ?? throw new InvalidOperationException("probe falhou");
            var audioIdx = EscolherAudio(filmeId, info.Audios);
            var audioJaCompativel = EscolhaAudio.JaCompativel(info.Audios.FirstOrDefault(a => a.Index == audioIdx));

            var args = FfmpegArgs.Remux(origem, tmp, audioIdx, info.Largura, info.Altura, audioJaCompativel);

            var psi = new ProcessStartInfo(_ffmpegPath);
            foreach (var a in args) psi.ArgumentList.Add(a);

            // Mesmo detector de órfão do HLS: pré-busca do próximo episódio que ninguém chegou
            // a assistir não pode remuxar o arquivo inteiro de graça.
            bool Travou()
            {
                var abortar = DeveAbortarRemux(filmeId);
                if (abortar) orfao = true;
                return abortar;
            }

            var relogio = Stopwatch.StartNew();
            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, _jobTimeout, Travou, ct);
            if (orfao)
            {
                RemuxOrfao(filmeId);
                DescartarTmp(tmp);
                return;
            }
            if (exit != 0) throw new InvalidOperationException($"ffmpeg exit {exit}: {stderr}");

            File.Move(tmp, final, overwrite: true);
            try { File.WriteAllText(CaminhoFaixaDoRemux(final), audioIdx?.ToString() ?? ""); } catch (IOException) { }
            _escrita.ContarArquivo(final);
            LogarVelocidadeDoRemux(filmeId, final, relogio.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // Shutdown do host OU troca manual de dublagem (DefinirPreferenciaAudio) — não
            // marca _remuxFalhas: fica retentável na próxima vez que alguém pedir /remux-status.
            _logger.LogInformation("Remux do filme {Id} interrompido.", filmeId);
            DescartarTmp(tmp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remuxar filme {Id}.", filmeId);
            _remuxFalhas[filmeId] = DateTime.UtcNow;
            DescartarTmp(tmp);
        }
        finally
        {
            _remuxJobs.TryRemove(filmeId, out _);
            _remuxQuente.TryRemove(filmeId, out _);
            if (_jobCancelRemux.TryRemove(filmeId, out var cts)) cts.Dispose();
            if (slotObtido) _slotEncoder.Release();
        }

        if (!ct.IsCancellationRequested) LimparRemuxCacheExcedente();
    }

    private async Task EsperarSlotAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _esperandoSlot);
        try { await _slotEncoder.WaitAsync(ct); }
        finally { Interlocked.Decrement(ref _esperandoSlot); }
    }

    /// <summary>A mesma pergunta nos dois pontos do remux (antes de começar e a cada ~2 s com o ffmpeg rodando): pré-aquecimento
    /// cede se alguém precisa do encoder ou a placa esquentou; remux pedido de verdade aborta se ninguém olha mais.</summary>
    private bool DeveAbortarRemux(int filmeId) => _remuxQuente.ContainsKey(filmeId) ? CederPreAquecimento() : SemInteresse(filmeId);

    /// <summary>Joga fora o remux parcial (cancelado, órfão ou falho), contando o que já foi gravado.</summary>
    private void DescartarTmp(string tmp)
    {
        _escrita.ContarArquivo(tmp);
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool CederPreAquecimento() => PoliticaPreAquecimento.DeveCeder(Volatile.Read(ref _esperandoSlot), _thermal.EstaThrottling);

    /// <summary>MB, segundos e MB/s do remux que acabou — pra dar pra ver, nos logs, se piorou (disco, térmica).</summary>
    private void LogarVelocidadeDoRemux(int filmeId, string arquivo, TimeSpan duracao)
    {
        try
        {
            var mb = new FileInfo(arquivo).Length / 1048576.0;
            var seg = Math.Max(0.001, duracao.TotalSeconds);
            _logger.LogInformation("Remux do filme {Id} pronto: {Mb:F0} MB em {Seg:F0} s ({Velocidade:F1} MB/s){Quente}.",
                filmeId, mb, seg, mb / seg, _remuxQuente.ContainsKey(filmeId) ? ", pré-aquecido" : "");
        }
        catch (IOException) { }
    }

    /// <summary>Sem job de HLS/remux, ninguém esperando slot e a placa fora do throttling térmico.</summary>
    public bool Ocioso => _jobs.IsEmpty && _remuxJobs.IsEmpty && Volatile.Read(ref _esperandoSlot) == 0 && !_thermal.EstaThrottling;

    /// <summary>Pré-aquece o remux só de quem precisa dele (vídeo ok, áudio não: o que já toca direto ou vai por HLS não
    /// tem o que aquecer) nem o que já foi assistido. Antes, descarta um remux pronto feito com outra faixa de áudio. Devolve true se começou um job.</summary>
    public async Task<bool> PreAquecerSeNecessarioAsync(int filmeId, string origem, bool jaAssistido, CancellationToken ct)
    {
        if (jaAssistido) return false;   // quem revê uma série não enche o cache de episódio que já viu (dar play continua funcionando)
        await DescartarRemuxDeOutraFaixaAsync(filmeId, origem, ct);
        if (await AnalisarCompatibilidadeAsync(origem, ct) != CompatibilidadeDireta.SoAudioIncompativel) return false;
        return PreAquecerRemux(filmeId, origem);
    }

    /// <summary>Começa o remux de <paramref name="filmeId"/> SEM ninguém ter pedido (ver <see cref="PoliticaPreAquecimento"/>).
    /// Só com o servidor ocioso e se o arquivo cabe no teto do cache sem expulsar nada. Devolve true se o job começou.</summary>
    public bool PreAquecerRemux(int filmeId, string origem)
    {
        if (!Ocioso || File.Exists(CaminhoRemux(filmeId))) return false;

        long tamanho;
        try { tamanho = new FileInfo(origem).Length; }
        catch (IOException) { return false; }
        var noCache = MedirCacheRemux().Bytes;
        if (!PoliticaPreAquecimento.CabeNoCache(noCache, tamanho, _remuxCacheMaxBytes))
        {
            _logger.LogInformation("Pré-aquecimento do filme {Id} adiado: o remux ({Mb:F0} MB) não cabe no teto do cache sem expulsar nada.", filmeId, tamanho / 1048576.0);
            return false;
        }

        var (status, _) = ObterStatusRemux(filmeId, origem, preAquecimento: true);
        if (status == RemuxStatus.Preparando) _logger.LogInformation("Pré-aquecendo o remux do filme {Id} ({Mb:F0} MB).", filmeId, tamanho / 1048576.0);
        return status == RemuxStatus.Preparando;
    }

    /// <summary>Remux abandonado: não marca _remuxFalhas (fica retentável quando alguém pedir de novo).</summary>
    private void RemuxOrfao(int filmeId)
    {
        if (_remuxQuente.ContainsKey(filmeId))
            _logger.LogInformation("Pré-aquecimento do remux do filme {Id} cedeu o lugar: outro job precisa do encoder ou a placa esquentou.", filmeId);
        else
            _logger.LogInformation("Remux do filme {Id} abortado: ninguém pediu status há mais de {Seg}s.",
                filmeId, _orphanTimeout.TotalSeconds);
        _ultimoAcesso.TryRemove(filmeId, out _);
    }
}
