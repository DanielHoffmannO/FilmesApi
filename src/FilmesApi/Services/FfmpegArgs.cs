namespace FilmesApi.Services;

/// <summary>Linhas de argumentos do ffmpeg pros jobs de remux e HLS. Funções puras (sem subir
/// processo), por isso testáveis direto — ver <c>FilmesApi.Tests</c>.</summary>
internal static class FfmpegArgs
{
    internal const int SegmentoSegundos = 6;

    /// <summary>Monta a linha de argumentos do ffmpeg pro job de <c>/remux</c> (vídeo copiado
    /// <c>-c:v copy</c>, áudio reencodado pra AAC estéreo — exceto quando
    /// <paramref name="audioJaCompativel"/>, ver <see cref="EscolhaAudio.JaCompativel"/>: copia também,
    /// sem gastar CPU decodificando+reencodando áudio que já estava bom). Pura e <c>internal</c>
    /// de propósito, mesma ideia do <see cref="FfmpegArgs.Hls"/>. <paramref name="largura"/>/
    /// <paramref name="altura"/> viram <c>-aspect largura:altura</c> quando os dois são &gt; 0
    /// — corrige metadado de proporção de pixel quebrado (SAR, ver
    /// <see cref="CompatibilidadeVideo.SarPrecisaCorrecao"/>) sem reencodar vídeo: <c>-aspect</c> só sobrescreve o
    /// DAR gravado no container, o stream-copy não toca no pixel. Aplicado sempre que
    /// conhecidos (não só quando o SAR está ruim) — é inofensivo num arquivo já correto e evita
    /// ter que replicar aqui a mesma decisão de "está ruim?" do <see cref="CompatibilidadeVideo.Classificar"/>.</summary>
    internal static List<string> Remux(
        string origem, string destino, int? audioStreamIndex, int largura = 0, int altura = 0, bool audioJaCompativel = false)
    {
        List<string> args = ["-y", "-i", origem, "-map", "0:v:0"];
        if (audioStreamIndex is int idx) args.AddRange(["-map", $"0:{idx}"]);
        args.Add("-c:v"); args.Add("copy");
        if (audioStreamIndex is not null)
            args.AddRange(audioJaCompativel ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k", "-ac", "2"]);
        if (largura > 0 && altura > 0) args.AddRange(["-aspect", $"{largura}:{altura}"]);
        // -f mp4 explícito: o nome real é "{id}.mp4.tmp" (renomeado pro final só depois
        // de completo), e o ffmpeg escolhe o muxer pela ÚLTIMA extensão — ".tmp" sozinho
        // não é reconhecido, então sem isso ele recusa nem abrir o arquivo de saída.
        args.AddRange(["-f", "mp4", "-movflags", "+faststart", destino]);
        return args;
    }

    /// <summary>Monta a linha de argumentos do ffmpeg pro job HLS. Pura e <c>internal</c>
    /// de propósito: dá pra testar a decisão de encode (ex.: o <c>-ac 2</c> obrigatório)
    /// sem subir processo — ver <c>FilmesApi.Tests</c>. <paramref name="largura"/>/
    /// <paramref name="altura"/>: mesma correção de <c>-aspect</c> do
    /// <see cref="FfmpegArgs.Remux"/>, ver comentário lá. <paramref name="audioJaCompativel"/>:
    /// ver <see cref="EscolhaAudio.JaCompativel"/> — copia o áudio em vez de reencodar quando a faixa já
    /// está num formato que o navegador aceita.</summary>
    internal static List<string> Hls(
        string origem, bool videoCompativel, bool usarRkmpp, int? audioStreamIndex, int? downscalePara, bool decodeHw,
        int largura = 0, int altura = 0, bool audioJaCompativel = false)
    {
        List<string> args = ["-y"];

        // -hwaccel/-hwaccel_output_format são opções de INPUT — têm que vir ANTES do -i
        // (diferente do -init_hw_device do caminho sem hw-decode, que prepara o device pro
        // filtro/output e fica depois). decodeHw só é true no 4K+downscale via rkmpp.
        if (decodeHw)
            args.AddRange(["-init_hw_device", "rkmpp=rk", "-filter_hw_device", "rk",
                           "-hwaccel", "rkmpp", "-hwaccel_output_format", "drm_prime"]);

        // -map explícito: sem isso o ffmpeg escolhe sozinho, por heurística de codec/canais,
        // qual stream de cada tipo entra no output — e essa heurística ignora idioma e pode
        // até ignorar a flag "default" do arquivo. Em filme dual-áudio (ex.: português +
        // francês) isso já produziu HLS com a faixa errada mesmo com português marcado como
        // default. Mapear video+áudio manualmente também impede que uma legenda embutida
        // incompatível (ex.: PGS/bitmap) entre sozinha no output e quebre o encode — a API
        // não serve legenda nenhuma, então nem faz sentido incluir.
        args.AddRange(["-i", origem, "-map", "0:v:0"]);
        if (audioStreamIndex is int audioIdx) args.AddRange(["-map", $"0:{audioIdx}"]);

        if (videoCompativel)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            if (usarRkmpp && decodeHw && downscalePara is int altHw)
            {
                // Frames já vêm decodificados em memória de VPU (drm_prime). O RGA escala e
                // converte pra nv12 (8-bit) sem round-trip pela CPU; h264_rkmpp consome direto.
                args.AddRange(["-vf", $"scale_rkrga=w=-2:h={altHw}:format=nv12", "-c:v", "h264_rkmpp"]);
            }
            else if (usarRkmpp)
            {
                // Decode em software; scale/format em CPU e sobe pro VPU só pra encodar.
                // format=nv12 força 8-bit 4:2:0 (mata HDR/10-bit, mas é o que os players aqui aguentam).
                var filtro = downscalePara is int alt
                    ? $"scale=-2:{alt},format=nv12,hwupload"
                    : "format=nv12,hwupload";
                args.AddRange(["-init_hw_device", "rkmpp=rk", "-filter_hw_device", "rk", "-vf", filtro, "-c:v", "h264_rkmpp"]);
            }
            else
            {
                // -pix_fmt yuv420p: nunca deixa passar 10-bit (High 10) pro output — navegador não toca.
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p"]);
                if (downscalePara is int alt2) args.AddRange(["-vf", $"scale=-2:{alt2}"]);
            }

            // Segmentação previsível mesmo com fps/keyframes irregulares da fonte — vale
            // pros dois encoders de reencode, só não faz sentido no remux acima (-c:v copy
            // só pode cortar em keyframes que já existem no arquivo original).
            args.AddRange(["-sc_threshold", "0", "-force_key_frames", $"expr:gte(t,n_forced*{SegmentoSegundos})"]);
        }

        // -ac 2: downmix pra estéreo. Áudio 5.1/7.1 (EAC3 de WEB-DL é o caso comum) reencodado
        // pra AAC multicanal sai sem channel_layout que o decoder AAC do Chrome (MSE/hls.js)
        // reconheça — e ele rejeita EM SILÊNCIO: a API responde 200, playlist e segments
        // existem, mas o vídeo não toca e não há erro em log nenhum. ffprobe e players desktop
        // toleram, então é quase impossível diagnosticar sem saber disso de antemão. Esse risco
        // só existe decodificando+reencodando de novo — se a faixa escolhida JÁ está num codec
        // bom com 2 canais ou menos (audioJaCompativel), copiar é tão seguro quanto o vídeo e
        // poupa CPU de decode+encode de áudio à toa (visto: ffmpeg a 140%+ de CPU só pra
        // reencodar um AAC estéreo pro mesmo AAC estéreo).
        if (audioStreamIndex is not null)
            args.AddRange(audioJaCompativel ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k", "-ac", "2"]);
        if (largura > 0 && altura > 0) args.AddRange(["-aspect", $"{largura}:{altura}"]);
        args.AddRange([
            "-f", "hls",
            "-hls_time", SegmentoSegundos.ToString(),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_flags", "temp_file+independent_segments",
            "-hls_segment_type", "mpegts",
            "-start_number", "0",
            "-hls_segment_filename", "seg_%05d.ts",
            "playlist.m3u8",
        ]);
        return args;
    }
}
