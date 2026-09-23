using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão: a decisão "toca cru / só precisa arrumar áudio / nem isso" viveu espalhada em
/// 3 métodos quase iguais (EhCompativelAsync, PodeTocarDireto, PrecisaSoRemuxAudioAsync) até
/// virar bug — um ficou desatualizado enquanto os outros mudavam. Unificada em
/// ClassificarCompatibilidade; estes testes existem pra pegar se isso voltar a acontecer.
/// </summary>
public class CompatibilidadeDiretaTests
{
    [Fact]
    public void Mp4_h264_aac_e_totalmente_compativel()
    {
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mp4", "h264", video10Bit: false, "aac");
        Assert.Equal(CompatibilidadeDireta.Compativel, r);
    }

    [Fact]
    public void Mkv_h264_eac3_precisa_so_de_remux_de_audio()
    {
        // O caso real de hoje: WEB-DL com vídeo h264 (ok) e áudio EAC3 5.1 (TV recusa).
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mkv", "h264", video10Bit: false, "eac3");
        Assert.Equal(CompatibilidadeDireta.SoAudioIncompativel, r);
    }

    [Fact]
    public void Mkv_com_video_e_audio_bons_nao_e_compativel_direto()
    {
        // Extensão restrita de propósito: MKV nunca é "Compativel" mesmo com codecs bons —
        // comportamento de sempre, só não pode virar SoAudioIncompativel (não precisa remux,
        // o áudio já está ok — cai pro /original mesmo, que players costumam aceitar).
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mkv", "h264", video10Bit: false, "aac");
        Assert.Equal(CompatibilidadeDireta.Incompativel, r);
    }

    [Fact]
    public void Video_incompativel_nunca_vira_soAudioIncompativel()
    {
        // Remux só copia vídeo (sem re-encode) — se o vídeo também precisa mudar, não é o
        // caso do /remux (isso é trabalho do HLS com reencode completo).
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mkv", "hevc", video10Bit: false, "eac3");
        Assert.Equal(CompatibilidadeDireta.Incompativel, r);
    }

    [Fact]
    public void Sem_faixa_de_audio_conta_como_audio_ok()
    {
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mp4", "h264", video10Bit: false, null);
        Assert.Equal(CompatibilidadeDireta.Compativel, r);
    }

    [Fact]
    public void Sem_video_e_incompativel()
    {
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mp4", null, video10Bit: false, "aac");
        Assert.Equal(CompatibilidadeDireta.Incompativel, r);
    }

    // Regressão: H.264 "High 10"/HEVC "Main 10" tem codec_name "compatível" mas o navegador
    // não decodifica 10-bit — tocava direto/remux com tela preta e ZERO erro em log.
    [Fact]
    public void Mp4_h264_10bit_nao_e_compativel_mesmo_com_audio_bom()
    {
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mp4", "h264", video10Bit: true, "aac");
        Assert.Equal(CompatibilidadeDireta.Incompativel, r);
    }

    [Fact]
    public void Video_10bit_com_audio_ruim_nao_vira_soAudioIncompativel()
    {
        // /remux só copia o vídeo (-c:v copy) — copiar um stream 10-bit não resolve nada,
        // então 10-bit não pode cair no caminho "só precisa arrumar áudio".
        var r = HlsTranscodeService.ClassificarCompatibilidade(".mp4", "h264", video10Bit: true, "eac3");
        Assert.Equal(CompatibilidadeDireta.Incompativel, r);
    }

    [Fact]
    public void VideoTocavelSemReencode_recusa_10bit_mesmo_com_codec_da_lista()
    {
        Assert.False(HlsTranscodeService.VideoTocavelSemReencode("h264", video10Bit: true));
        Assert.True(HlsTranscodeService.VideoTocavelSemReencode("h264", video10Bit: false));
    }

    // Regressão real: usuário relatou áudio em inglês E português tocando JUNTO na tela normal
    // (index.html), só resolvendo ao trocar de faixa manualmente pelo seletor nativo do
    // <video>. Causa: um .mp4 com 2 faixas de áudio (ambas com codec "bom", ex. AAC) passava
    // como Compativel -- /stream serve o arquivo cru sem nenhum -map, então as DUAS faixas iam
    // juntas pro navegador. O fix de dual-áudio anterior (EscolherStreamAudio + -map explícito)
    // só cobria os caminhos que passam pelo ffmpeg (HLS reencode e /remux) -- nunca o /stream
    // direto, que por definição não processa nada.
    [Fact]
    public void Duas_faixas_de_audio_nunca_e_compativel_mesmo_com_codec_bom()
    {
        var r = HlsTranscodeService.ClassificarCompatibilidade(
            ".mp4", "h264", video10Bit: false, "aac", quantidadeFaixasAudio: 2);
        Assert.Equal(CompatibilidadeDireta.SoAudioIncompativel, r);
    }

    [Fact]
    public void Duas_faixas_de_audio_com_video_incompativel_continua_incompativel()
    {
        // Mesma regra de sempre pra vídeo ruim: /remux só copia vídeo, não serve pra esse caso.
        var r = HlsTranscodeService.ClassificarCompatibilidade(
            ".mp4", "hevc", video10Bit: false, "aac", quantidadeFaixasAudio: 2);
        Assert.Equal(CompatibilidadeDireta.Incompativel, r);
    }

    [Fact]
    public void Uma_faixa_de_audio_continua_compativel_como_antes()
    {
        // Comportamento de sempre preservado -- só a chamada explícita com 1 (ou omitida,
        // default) segue tocando direto.
        var r = HlsTranscodeService.ClassificarCompatibilidade(
            ".mp4", "h264", video10Bit: false, "aac", quantidadeFaixasAudio: 1);
        Assert.Equal(CompatibilidadeDireta.Compativel, r);
    }
}
