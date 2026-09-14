using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Parsing do JSON do <c>ffprobe -show_streams -show_format -of json</c>, sem rodar processo
/// nenhum. Cobre as duas causas confirmadas de "tela preta sem erro nenhum": capa/poster
/// embutido (attached_pic) confundido com o vídeo de verdade, e vídeo 10-bit que o navegador
/// não decodifica mesmo com codec_name "compatível".
/// </summary>
public class MediaProbeServiceTests
{
    [Fact]
    public void Video_8bit_comum_nao_e_marcado_10bit()
    {
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "pix_fmt": "yuv420p" },
                { "index": 1, "codec_type": "audio", "codec_name": "aac" }
              ], "format": { "duration": "5400.0" } }
            """);
        Assert.Equal("h264", info.VideoCodec);
        Assert.False(info.Video10Bit);
        Assert.Equal(1920, info.Largura);
        Assert.Equal(1080, info.Altura);
        Assert.Equal(5400.0, info.DuracaoSegundos);
    }

    [Fact]
    public void Pix_fmt_10le_marca_video_10bit()
    {
        // Caso real do bug: H.264 High 10 Profile em .mp4 tocava direto com tela preta.
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
                  "profile": "High 10", "pix_fmt": "yuv420p10le" }
              ] }
            """);
        Assert.True(info.Video10Bit);
    }

    [Fact]
    public void Bits_per_raw_sample_e_fallback_quando_pix_fmt_nao_indica()
    {
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160,
                  "pix_fmt": "yuv420p", "bits_per_raw_sample": "10" }
              ] }
            """);
        Assert.True(info.Video10Bit);
    }

    [Fact]
    public void Attached_pic_e_ignorado_ao_escolher_o_video()
    {
        // Comum em rip de anime/música: capa embutida como 1º stream "video" (mjpeg de 1
        // frame). Sem o filtro, isso virava o "VideoCodec" e -map 0:v:0 mapeava a imagem
        // estática em vez do vídeo de verdade.
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "mjpeg", "width": 500, "height": 500,
                  "disposition": { "attached_pic": 1 } },
                { "index": 1, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
                  "pix_fmt": "yuv420p" },
                { "index": 2, "codec_type": "audio", "codec_name": "aac" }
              ] }
            """);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(1920, info.Largura);
        Assert.False(info.Video10Bit);
    }

    [Fact]
    public void So_tem_attached_pic_entao_nao_ha_video()
    {
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "mjpeg", "width": 500, "height": 500,
                  "disposition": { "attached_pic": 1 } },
                { "index": 1, "codec_type": "audio", "codec_name": "aac" }
              ] }
            """);
        Assert.Null(info.VideoCodec);
    }

    [Fact]
    public void Duracao_ausente_ou_invalida_e_null()
    {
        var info = MediaProbeService.Parsear("""{ "streams": [], "format": {} }""");
        Assert.Null(info.DuracaoSegundos);
    }

    [Fact]
    public void Faixa_de_audio_com_idioma_e_default_e_lida()
    {
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 1, "codec_type": "audio", "codec_name": "ac3",
                  "tags": { "language": "por" }, "disposition": { "default": 1 } },
                { "index": 2, "codec_type": "audio", "codec_name": "dts",
                  "tags": { "language": "fra" }, "disposition": { "default": 0 } }
              ] }
            """);
        Assert.Equal(2, info.Audios.Count);
        Assert.Equal("por", info.Audios[0].Idioma);
        Assert.True(info.Audios[0].Default);
        Assert.False(info.Audios[1].Default);
    }

    [Fact]
    public void Legenda_bitmap_e_listada_com_codec_correto()
    {
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 3, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle",
                  "tags": { "language": "por" }, "disposition": { "forced": 0, "default": 0 } }
              ] }
            """);
        Assert.Single(info.Legendas);
        Assert.Equal("hdmv_pgs_subtitle", info.Legendas[0].Codec);
        Assert.Equal(0, info.Legendas[0].IdxRelativo);
    }
}
