namespace FilmesApi.Services;

/// <summary>Qual faixa de áudio vai pro HLS/remux — função pura sobre o resultado do probe.</summary>
internal static class EscolhaAudio
{
    private static readonly string[] IdiomasAudioPreferidos = ["por", "pt", "pob"];

    /// <summary>Idioma ffprobe ("por", "pt", "pob") é português?</summary>
    internal static bool EhPortugues(string? idioma) =>
        idioma is not null && IdiomasAudioPreferidos.Contains(idioma.ToLowerInvariant());

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng",
        ["eng"] = "eng",
        ["pt"] = "por",
        ["por"] = "por",
        ["pob"] = "por",
        ["pt-br"] = "por",
        ["ptbr"] = "por",
        ["es"] = "spa",
        ["spa"] = "spa",
        ["fr"] = "fra",
        ["fra"] = "fra",
        ["fre"] = "fra",
        ["de"] = "deu",
        ["deu"] = "deu",
        ["ger"] = "deu",
        ["it"] = "ita",
        ["ita"] = "ita",
        ["ja"] = "jpn",
        ["jpn"] = "jpn",
    };

    /// <summary>Código de idioma (do ffprobe ou digitado numa tela) → o código canônico de 3 letras
    /// (<c>en</c>/<c>eng</c> → <c>eng</c>; <c>pt</c>/<c>por</c>/<c>pob</c> → <c>por</c>). Null se não reconhece.</summary>
    internal static string? NormalizarIdioma(string? idioma) =>
        idioma is not null && Aliases.TryGetValue(idioma.Trim(), out var canonico) ? canonico : null;

    // ─── Dublagem por IA (scripts/dublar: acrescenta ao MKV uma faixa AAC language=por, título "Português (IA)") ───

    /// <summary>A faixa é a "dublagem por IA" (voz sintética em português por cima do original)? Decide só pelo
    /// título da faixa — ver <see cref="TituloIndicaDubladoIa"/>. Faixa null ou sem título: não é.</summary>
    internal static bool EhDubladoIa(FaixaAudio? faixa) => TituloIndicaDubladoIa(faixa?.Titulo);

    /// <summary>O título marca dublagem por IA quando contém <c>IA)</c> (o fim de "(IA)" — "Português (IA)",
    /// "Dublagem (IA)", "Voz IA)"), sem diferenciar maiúscula nem acento. O <c>IA</c> precisa começar uma palavra:
    /// "Português (Itália)", "(Bolívia)" e "(Colômbia)" também terminam em <c>ia)</c> e NÃO são IA.</summary>
    internal static bool TituloIndicaDubladoIa(string? titulo)
    {
        if (string.IsNullOrEmpty(titulo)) return false;
        var t = string.Concat(titulo.Select(MediaNomeParser.SemAcentoMinuscula));   // tabela ordinal: o projeto roda sem ICU
        for (var i = t.IndexOf("ia)", StringComparison.Ordinal); i >= 0; i = t.IndexOf("ia)", i + 1, StringComparison.Ordinal))
            if (i == 0 || !char.IsLetterOrDigit(t[i - 1])) return true;
        return false;
    }

    /// <summary>A primeira faixa que NÃO é dublagem por IA; se só há IA, a primeira IA; null se a lista é vazia.
    /// Dublagem de verdade (humana) sempre vence a sintética.</summary>
    private static FaixaAudio? PreferirNaoIa(IEnumerable<FaixaAudio> faixas)
    {
        FaixaAudio? ia = null;
        foreach (var f in faixas)
        {
            if (!EhDubladoIa(f)) return f;
            ia ??= f;
        }
        return ia;
    }

    /// <summary>Índice global (pra <c>-map</c>) da faixa de áudio que vai pro HLS/remux. Do mais forte ao
    /// mais fraco: a escolha manual do episódio (controle remoto — ver
    /// <see cref="HlsTranscodeService.DefinirPreferenciaAudio"/>) se a faixa existe de verdade no arquivo; o
    /// idioma padrão da série, se o arquivo tem uma faixa nele; português — rip "dual áudio" costuma marcar a
    /// faixa errada como default —; a default; a primeira. null se não há áudio. Dentro de um mesmo idioma
    /// (série ou português), a faixa que não é dublagem por IA (<see cref="EhDubladoIa(FaixaAudio?)"/>) vence;
    /// a IA só é escolhida quando é a única naquele idioma. Função pura sobre o probe
    /// (testável sem ffprobe de verdade, ver <c>FilmesApi.Tests</c>).</summary>
    internal static int? Escolher(IReadOnlyList<FaixaAudio> audios, int? preferenciaManual = null, string? idiomaDaSerie = null)
    {
        if (audios.Count == 0) return null;
        if (preferenciaManual is int pref && audios.Any(a => a.Index == pref)) return pref;
        if (NormalizarIdioma(idiomaDaSerie) is string idiomaSerie
            && PreferirNaoIa(audios.Where(a => NormalizarIdioma(a.Idioma) == idiomaSerie)) is { } daSerie)
            return daSerie.Index;
        return PreferirNaoIa(audios.Where(a => EhPortugues(a.Idioma)))?.Index
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
