namespace FilmesApi.Services;

/// <summary>Decisão pura "dá pra tocar este arquivo sem transcode?" — codecs que o navegador
/// aceita, SAR quebrado, dual-áudio. Separada do probe (I/O) pra testar direto.</summary>
internal static class CompatibilidadeVideo
{
    internal static readonly string[] VideoCodecsCompativeis = ["h264", "vp9", "av1"];
    internal static readonly string[] AudioCodecsCompativeis = ["aac", "mp3", "opus"];

    /// <summary>SAR (proporção de pixel) diferente de "1:1"/"0:1"/ausente é metadado quebrado —
    /// sobra de um encode anterior nunca resetado (caso real: "Diários de Um Vampiro" T8,
    /// release LAPUMiAFiLMES.COM, SAR 40:33 herdado de uma fonte DVD NTSC num arquivo já
    /// reencodado pra 1280x720 "widescreen quadrado"). O navegador confia nesse metadado pra
    /// desenhar o vídeo — o resultado observado foi o vídeo não preencher a tela mesmo com a
    /// resolução em pixel certa. "0:1" é o valor que o ffprobe reporta quando o bitstream não
    /// informa SAR nenhum (a maioria dos arquivos) — tratado como normal, não como "ausente
    /// então suspeito".</summary>
    internal static bool SarPrecisaCorrecao(string? sampleAspectRatio) =>
        sampleAspectRatio is not (null or "1:1" or "0:1");

    /// <summary>Vídeo tocável sem reencodar (stream direto OU remux <c>-c:v copy</c>): codec
    /// numa lista curta E não 10-bit — 10-bit (<c>High 10</c>/<c>Main 10</c>) o navegador não
    /// decodifica mesmo com <c>codec_name</c> "compatível", e copiar o stream não muda isso.
    /// Compartilhado entre <see cref="CompatibilidadeVideo.Classificar"/> (decide /stream e /remux)
    /// e <c>HlsTranscodeService.TranscodificarHlsAsync</c> (decide remux vs reencode dentro do HLS) — as
    /// duas decisões já divergiram uma vez (<c>b19d789</c>) por checarem isso cada uma do seu jeito.</summary>
    internal static bool VideoTocavelSemReencode(string? videoCodec, bool video10Bit) =>
        videoCodec is not null && VideoCodecsCompativeis.Contains(videoCodec) && !video10Bit;

    /// <summary>Decisão pura de compatibilidade, separada do probe (I/O) pra dar pra testar
    /// direto — mesma ideia do <see cref="FfmpegArgs.Hls"/>. Ver <see cref="HlsTranscodeService.AnalisarCompatibilidadeAsync"/>
    /// pro caminho real, que lê o arquivo com ffprobe antes de chamar isso.</summary>
    internal static CompatibilidadeDireta Classificar(
        string extensao, string? videoCodec, bool video10Bit, string? primeiroAudioCodec,
        int quantidadeFaixasAudio = 1, string? sampleAspectRatio = null)
    {
        var videoOk = CompatibilidadeVideo.VideoTocavelSemReencode(videoCodec, video10Bit);
        // 2+ faixas de áudio (dual-áudio) nunca é "Compativel" mesmo com codec bom — /stream
        // serve o arquivo cru sem `-map` nenhum, e o navegador não isola só a faixa default
        // de forma confiável (chegou a tocar as duas faixas juntas, inglês+português, até o
        // usuário trocar manualmente pelo seletor nativo do <video>). /remux já resolve isso
        // de verdade: EscolhaAudio.Escolher elege 1 faixa (idioma pt > default > primeira) e
        // remuxa só ela — então força pelo mesmo caminho que áudio incompatível usaria.
        var audioOk = quantidadeFaixasAudio <= 1
            && (primeiroAudioCodec is null || AudioCodecsCompativeis.Contains(primeiroAudioCodec));
        // SAR quebrado (ver CompatibilidadeVideo.SarPrecisaCorrecao) força o mesmo caminho de /remux mesmo com
        // vídeo/áudio ok — é lá que o -aspect corrige o metadado sem reencodar vídeo.
        var sarOk = !CompatibilidadeVideo.SarPrecisaCorrecao(sampleAspectRatio);

        // "Compativel" (toca cru, sem processar nada) só vale pra containers que o <video>
        // entende sem drama — extensão restrita de propósito, MKV mesmo com codecs bons fica
        // de fora (comportamento de sempre, ver histórico do antigo EhCompativelAsync).
        var containerOk = extensao.ToLowerInvariant() is ".mp4" or ".webm" or ".mov" or ".m4v";

        if (containerOk && videoOk && audioOk && sarOk) return CompatibilidadeDireta.Compativel;
        if (videoOk && (!audioOk || !sarOk)) return CompatibilidadeDireta.SoAudioIncompativel;
        return CompatibilidadeDireta.Incompativel;
    }
}
