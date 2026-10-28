using FilmesApi.Models;

namespace FilmesApi.Services;

public partial class HlsTranscodeService
{
    /// <summary>Faixas de áudio do arquivo ("dublagens" disponíveis), com qual delas o
    /// remux/HLS usaria agora — pro controle.html listar e já destacar a atual. Ver
    /// <see cref="EscolhaAudio.Escolher"/>.</summary>
    public async Task<IReadOnlyList<AudioInfo>> ListarAudiosAsync(int filmeId, string arquivoAbsoluto, CancellationToken ct)
    {
        var info = await _probe.InspecionarAsync(arquivoAbsoluto, ct);
        if (info is null || info.Audios.Count == 0) return [];

        var escolhida = EscolherAudio(filmeId, info.Audios);
        return info.Audios
            .Select(a => new AudioInfo(a.Index, a.Codec, a.Idioma, a.Default, a.Index == escolhida))
            .ToList();
    }

    /// <summary>Troca a faixa de áudio preferida manualmente pra este filme (controle remoto —
    /// "dublagem" em <c>controle.html</c>), sobrepondo a escolha automática por idioma de
    /// <see cref="EscolhaAudio.Escolher"/>. Cancela um job de HLS/remux em andamento pra este
    /// filme (se houver) e apaga o cache já pronto (HLS e /remux), pra forçar regerar com a
    /// faixa nova na próxima vez que /hls/playlist.m3u8 ou /remux-status for pedido — o áudio
    /// vai dentro dos segments/arquivo, não dá pra trocar sem reprocessar.</summary>
    public void DefinirPreferenciaAudio(int filmeId, int audioStreamIndex)
    {
        _prefs.DefinirFaixaDoEpisodio(filmeId, audioStreamIndex);
        DescartarCaches(filmeId);
    }

    /// <summary>A faixa que vale pra este episódio: manual do episódio > idioma da série > regra automática
    /// (<see cref="EscolhaAudio.Escolher"/>). TUDO que escolhe faixa passa por aqui: HLS, remux, lista de /audios
    /// e, por consequência, a legenda automática.</summary>
    private int? EscolherAudio(int filmeId, IReadOnlyList<FaixaAudio> audios)
    {
        var (manual, idiomaDaSerie) = _prefs.Resolver(filmeId);
        return EscolhaAudio.Escolher(audios, manual, idiomaDaSerie);
    }

    private static string CaminhoFaixaDoRemux(string remux) => remux + NomesDeCache.SufixoFaixa;

    /// <summary>Remux em cache feito com OUTRA faixa de áudio do que a de hoje (a série ganhou um idioma
    /// padrão depois que o episódio foi remuxado): apaga o .mp4 pra que o próximo pedido o refaça. Só mexe no
    /// que está parado — remux em andamento, ou sem a anotação da faixa (cache antigo), fica como está.
    /// Chamado ao abrir o episódio (<c>/remux-status</c>), não quando a preferência muda: quem está
    /// assistindo não é derrubado, e episódio que ninguém abre não custa nada.</summary>
    public async Task DescartarRemuxDeOutraFaixaAsync(int filmeId, string origem, CancellationToken ct)
    {
        var remux = CaminhoRemux(filmeId);
        var anotacao = CaminhoFaixaDoRemux(remux);
        if (!File.Exists(remux) || !File.Exists(anotacao)) return;

        var info = await _probe.InspecionarAsync(origem, ct);
        if (info is null) return;
        var atual = EscolherAudio(filmeId, info.Audios);
        string feitoComFaixa;
        try { feitoComFaixa = (await File.ReadAllTextAsync(anotacao, ct)).Trim(); }
        catch (IOException) { return; }
        if (feitoComFaixa == atual?.ToString()) return;

        lock (_remuxLock)
        {
            if (_remuxJobs.ContainsKey(filmeId)) return;
            try { File.Delete(remux); File.Delete(anotacao); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
        _logger.LogInformation("Remux do filme {Id} descartado: foi feito com a faixa {Antes} e o áudio de hoje é {Agora}.", filmeId, feitoComFaixa, atual);
    }
}
