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
}
