using System.Diagnostics;

namespace FilmesApi.Services;

public partial class HlsTranscodeService
{
    /// <summary>Retorna o status atual e, quando aplicável, o caminho pronto para servir
    /// (arquivo original se compatível, ou playlist.m3u8 se HLS já tem algo pronto).</summary>
    public async Task<(StreamStatus Status, string? Path)> ObterStatusAsync(int filmeId, string arquivoOriginal, CancellationToken ct)
    {
        if (await AnalisarCompatibilidadeAsync(arquivoOriginal, ct) == CompatibilidadeDireta.Compativel)
            return (StreamStatus.Compativel, arquivoOriginal);

        RegistrarInteresse(filmeId);
        var dir = DiretorioCache(filmeId);
        var playlist = CaminhoPlaylist(filmeId);

        // Toda a decisão (falha permanente / cache completo / job em andamento / começar do
        // zero) precisa ser uma única seção síncrona e atômica sob o mesmo lock que protege
        // as mutações do job (TranscodificarHlsAsync) — senão duas requisições concorrentes
        // podem, por exemplo, ler "sem job, sem ENDLIST" bem no instante em que o job estava
        // terminando com sucesso, e apagar um cache recém-completo pra recomeçar à toa.
        lock (_decisaoLock)
        {
            if (_falhas.TryGetValue(filmeId, out var falhouEm))
            {
                if (DateTime.UtcNow - falhouEm < _falhaCooldown)
                    return (StreamStatus.Erro, null);
                _falhas.TryRemove(filmeId, out _);  // cooldown passou — deixa tentar de novo
            }

            // Uma vez confirmado completo, nunca mais muda — evita reler o playlist.m3u8
            // inteiro do disco (que cresce com o número de segments) em toda chamada futura,
            // inclusive nas próximas vezes que alguém reabrir o mesmo filme já cacheado.
            if (_completos.ContainsKey(filmeId))
            {
                RegistrarAcesso(playlist);
                return (StreamStatus.Disponivel, playlist);
            }

            if (File.Exists(playlist) && File.ReadAllText(playlist).Contains("#EXT-X-ENDLIST"))
            {
                _completos[filmeId] = true;
                RegistrarAcesso(playlist);
                return (StreamStatus.Disponivel, playlist);
            }

            if (_jobs.ContainsKey(filmeId))
            {
                var temSegmento = TemSegmento(dir);
                if (temSegmento) RegistrarAcesso(playlist);
                return (temSegmento ? StreamStatus.Disponivel : StreamStatus.Preparando, temSegmento ? playlist : null);
            }

            // Sem job vivo neste processo e sem cache completo: uma playlist parcial
            // encontrada aqui só pode ser resto de um job interrompido (crash/restart) —
            // não é confiável, então começa do zero.
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_pararToken);
            _jobCancelHls[filmeId] = cts;
            _jobs[filmeId] = Task.Run(() => TranscodificarHlsAsync(filmeId, arquivoOriginal, cts.Token), CancellationToken.None);
            _ultimoAcesso[filmeId] = DateTime.UtcNow;  // já conta como interesse — o job acabou de nascer
            return (StreamStatus.Preparando, null);
        }
    }

    private static bool TemSegmento(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, NomesDeCache.Segmentos).Any();
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private async Task TranscodificarHlsAsync(int filmeId, string origem, CancellationToken ct)
    {
        var dir = DiretorioCache(filmeId);

        // Espera a placa esfriar ANTES de tomar o slot: com MaxConcurrentTranscodeJobs > 1,
        // segurar só depois do slot deixaria os dois jobs entrarem e rodarem quentes juntos —
        // o gate só pegaria o terceiro. Aqui, um job novo não ocupa slot enquanto a placa
        // está no teto.
        var slotObtido = false;
        try
        {
            await _thermal.AguardarResfriamentoAsync(ct);
            await EsperarSlotAsync(ct);
            slotObtido = true;

            // Rechecagem: o slot pode ter demorado a liberar e a placa esquentado de novo
            // (o job anterior encerrou quente). Barato quando já está fria.
            await _thermal.AguardarResfriamentoAsync(ct);

            // Uma leitura de ffprobe só (cacheada): codec, resolução e faixa de áudio.
            var info = await _probe.InspecionarAsync(origem, CancellationToken.None)
                       ?? throw new InvalidOperationException("ffprobe não conseguiu ler o arquivo.");
            var videoCompativel = CompatibilidadeVideo.VideoTocavelSemReencode(info.VideoCodec, info.Video10Bit);

            // 4K/UHD decodificado + encodado em software trava o Radxa por minutos e esquenta
            // a placa. Se vamos reencodar e a entrada passa do teto, reduz a resolução no
            // filtro do ffmpeg — corta o custo de encode drasticamente. Perde resolução,
            // ganha estabilidade.
            int? downscalePara = null;
            if (!videoCompativel && _alturaMaxReencode > 0 && info.Altura > _alturaMaxReencode)
            {
                downscalePara = _alturaMaxReencode;
                _logger.LogWarning(
                    "Filme {Id}: entrada {W}x{H} — reencode com downscale pra {Alvo}p (4K+ em software sobrecarrega o servidor).",
                    filmeId, info.Largura, info.Altura, _alturaMaxReencode);
            }

            var usarRkmpp = !videoCompativel && await _rkmpp.DisponivelAsync();
            var audioStreamIndex = EscolherAudio(filmeId, info.Audios);
            var audioJaCompativel = EscolhaAudio.JaCompativel(info.Audios.FirstOrDefault(a => a.Index == audioStreamIndex));

            // Decode por hardware só faz diferença (e só vale o risco) no 4K com downscale —
            // 1080p decodifica barato em software. Fallback em cascata: hw-decode+hw-encode →
            // sw-decode+hw-encode → libx264. Cada nível apaga e recria o dir antes de tentar.
            var decodeHw = usarRkmpp && _rkmppDecodeHw && downscalePara is not null;

            var (exitCode, stderr, orfao, travouSemProgresso) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw, info.Largura, info.Altura, audioJaCompativel, ct);

            if (!orfao && decodeHw && exitCode != 0)
            {
                _logger.LogWarning("rkmpp com hwaccel de decode falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando rkmpp só no encode.",
                    filmeId, exitCode, stderr);
                LimparDir(dir);
                (exitCode, stderr, orfao, travouSemProgresso) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw: false, info.Largura, info.Altura, audioJaCompativel, ct);
            }

            if (ContaComoFalhaDeEncoder(orfao, travouSemProgresso) && usarRkmpp)
            {
                _rkmpp.RegistrarResultado(exitCode == 0);
                if (exitCode != 0)
                {
                    _logger.LogWarning("Encode via rkmpp falhou pro filme {Id} (exit {Code}): {Stderr}. Tentando de novo com libx264.",
                        filmeId, exitCode, stderr);
                    LimparDir(dir);
                    (exitCode, stderr, orfao, travouSemProgresso) = await RunFfmpegHlsAsync(filmeId, origem, dir, videoCompativel, usarRkmpp: false, audioStreamIndex, downscalePara, decodeHw: false, info.Largura, info.Altura, audioJaCompativel, ct);
                }
            }

            if (orfao)
            {
                // Ninguém mais assistindo — aborta sem marcar _falhas, pra ficar retentável
                // quando/se abrirem de novo. LimparCacheExcedente() no fim é pulado de propósito.
                _logger.LogInformation("Transcode do filme {Id} abortado: ninguém pediu status/segmento há mais de {Seg}s.",
                    filmeId, _orphanTimeout.TotalSeconds);
                LimparDir(dir);
                _ultimoAcesso.TryRemove(filmeId, out _);
                return;
            }

            if (exitCode != 0)
                throw new InvalidOperationException($"ffmpeg saiu com código {exitCode}: {stderr}");

            // Remux stream-copy = cópia quase idêntica do original (sem ganho). Marca pra
            // eviction preferir despejar esse tipo primeiro quando o cache passar do teto.
            if (videoCompativel)
                try { File.WriteAllText(Path.Combine(dir, NomesDeCache.MarcadorRemux), ""); } catch (IOException) { }
            _escrita.ContarPasta(dir);
        }
        catch (OperationCanceledException)
        {
            // Shutdown do host, placa quente demais por tempo demais, OU troca manual de
            // dublagem (DefinirPreferenciaAudio cancela o job em andamento). Não marca
            // _falhas: fica retentável no próximo boot / próxima abertura / próxima tentativa.
            _logger.LogInformation("Transcode do filme {Id} interrompido.", filmeId);
            try { lock (_decisaoLock) { _escrita.ContarPasta(dir); if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao transcodificar filme {Id} para HLS.", filmeId);
            lock (_decisaoLock)
            {
                _escrita.ContarPasta(dir);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                _falhas[filmeId] = DateTime.UtcNow;
            }
        }
        finally
        {
            lock (_decisaoLock) { _jobs.TryRemove(filmeId, out _); }
            if (_jobCancelHls.TryRemove(filmeId, out var cts)) cts.Dispose();
            if (slotObtido) _slotEncoder.Release();
        }

        if (!ct.IsCancellationRequested) LimparCacheExcedente();
    }

    /// <summary>Zera o dir de cache antes de uma nova tentativa de encode. Sob o mesmo lock
    /// que protege as leituras de ObterStatusAsync — senão uma checagem concorrente pode ler
    /// o diretório no meio da troca (segments parciais já podem ter sido servidos).</summary>
    private void LimparDir(string dir)
    {
        lock (_decisaoLock)
        {
            _escrita.ContarPasta(dir);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
    }

    private async Task<(int ExitCode, string Stderr, bool Orfao, bool TravouSemProgresso)> RunFfmpegHlsAsync(
        int filmeId, string origem, string dir, bool videoCompativel, bool usarRkmpp, int? audioStreamIndex, int? downscalePara, bool decodeHw,
        int largura, int altura, bool audioJaCompativel, CancellationToken ct)
    {
        var args = FfmpegArgs.Hls(origem, videoCompativel, usarRkmpp, audioStreamIndex, downscalePara, decodeHw, largura, altura, audioJaCompativel);

        var psi = new ProcessStartInfo(_ffmpegPath) { WorkingDirectory = dir };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // Detector de travamento: se a contagem de segments não muda por _stallTimeout, o
        // ffmpeg está pendurado (já aconteceu com alguns arquivos). Mata cedo em vez de
        // segurar o slot de encode pelo _jobTimeout inteiro (6h).
        var ultimaContagem = -1;
        var ultimoProgresso = DateTime.UtcNow;
        var orfao = false;
        var travouSemProgresso = false;
        bool Travou()
        {
            // Ninguém pede status/segmento deste filme há muito tempo — quem pediu desistiu.
            // Aborta pra não fritar a placa 1h transcodificando um 4K que ninguém vai ver.
            if (SemInteresse(filmeId))
            {
                orfao = true;
                return true;
            }

            int n;
            try { n = Directory.EnumerateFiles(dir, NomesDeCache.Segmentos).Count(); }
            catch { return false; }
            if (n != ultimaContagem) { ultimaContagem = n; ultimoProgresso = DateTime.UtcNow; return false; }
            if (DateTime.UtcNow - ultimoProgresso <= _stallTimeout) return false;

            // Travou de verdade (sem progresso) — diferente do órfão acima. Não é falha do
            // encoder (disco lento, placa quente, arquivo pesado): ver ContaComoFalhaDeEncoder.
            travouSemProgresso = true;
            return true;
        }

        var (exitCode, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, _jobTimeout, Travou, ct);
        return (exitCode, stderr, orfao, travouSemProgresso);
    }

    /// <summary>Um job morto pelo detector de travamento (sem progresso por <c>_stallTimeout</c>)
    /// não é uma falha do encoder de hardware — pode ser disco/mount lento, placa quente ou só
    /// um arquivo pesado. Só um <c>exitCode</c> genuinamente diferente de zero deveria contar
    /// contra o rkmpp (o caso de órfão já era filtrado à parte). Extraído e testado à parte
    /// porque é fácil "simplificar" essa condição de volta pra só <c>!orfao</c> sem perceber que
    /// isso desliga a VPU pelo resto do processo depois de 3 travamentos que nada têm a ver com
    /// o hardware — degradação progressiva que só um restart do container conserta.</summary>
    internal static bool ContaComoFalhaDeEncoder(bool orfao, bool travouSemProgresso) => !orfao && !travouSemProgresso;
}
