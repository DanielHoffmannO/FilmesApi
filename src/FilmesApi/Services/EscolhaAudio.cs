namespace FilmesApi.Services;

/// <summary>Qual faixa de áudio vai pro HLS/remux — função pura sobre o resultado do probe.</summary>
internal static class EscolhaAudio
{
    private static readonly string[] IdiomasAudioPreferidos = ["por", "pt", "pob"];

    /// <summary>Idioma ffprobe ("por", "pt", "pob") é português?</summary>
    internal static bool EhPortugues(string? idioma) =>
        idioma is not null && IdiomasAudioPreferidos.Contains(idioma.ToLowerInvariant());

    /// <summary>Índice global (pra <c>-map</c>) da faixa de áudio que vai pro HLS/remux. Se
    /// <paramref name="preferenciaManual"/> aponta pra uma faixa que existe de verdade nesse
    /// arquivo, usa ela (controle remoto escolheu uma "dublagem" — ver
    /// <see cref="HlsTranscodeService.DefinirPreferenciaAudio"/>); senão prioridade automática: primeiro uma em
    /// português — rip "dual áudio" costuma marcar a faixa errada como default —, senão a
    /// default, senão a primeira. null se não há áudio. Função pura sobre o probe (testável
    /// sem ffprobe de verdade, ver <c>FilmesApi.Tests</c>).</summary>
    internal static int? Escolher(IReadOnlyList<FaixaAudio> audios, int? preferenciaManual = null)
    {
        if (audios.Count == 0) return null;
        if (preferenciaManual is int pref && audios.Any(a => a.Index == pref)) return pref;
        return audios.Where(a => a.Idioma is not null && IdiomasAudioPreferidos.Contains(a.Idioma))
                .Select(a => (int?)a.Index).FirstOrDefault()
            ?? audios.Where(a => a.Default).Select(a => (int?)a.Index).FirstOrDefault()
            ?? audios[0].Index;
    }

    /// <summary>A faixa escolhida por <see cref="EscolhaAudio.Escolher"/> já está num formato que
    /// o navegador toca sem drama (codec da lista + mono/estéreo) — nesse caso dá pra copiar em
    /// vez de reencodar. O reencode pra AAC estéreo existe só pra dois problemas: codec
    /// incompatível (EAC3/DTS) e o bug do AAC multicanal sem channel_layout que o Chrome
    /// reconheça (ver comentário grande sobre <c>-ac 2</c> em <see cref="FfmpegArgs.Hls"/>)
    /// — nenhum dos dois existe quando a faixa já chega com codec bom e 2 canais ou menos. Canal
    /// desconhecido (ffprobe não informou) conta como "não sei, não arrisca" — fica no reencode.</summary>
    internal static bool JaCompativel(FaixaAudio? audio) =>
        audio?.Codec is string codec && CompatibilidadeVideo.AudioCodecsCompativeis.Contains(codec) && audio.Canais is int c && c <= 2;
}
