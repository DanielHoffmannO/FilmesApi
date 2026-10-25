using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>Idioma de áudio padrão por série ("O Mentalista" em inglês, o resto em português). Caso real: 70
/// episódios MKV H.264 com 2 faixas AC3 estéreo, <c>por</c> (default) e <c>eng</c>, e nenhuma legenda embutida —
/// a legenda vem de um <c>.por.srt</c> gerado por fora, ao lado do vídeo.</summary>
public class AudioPadraoPorSerieTests : IDisposable
{
    private const string Pasta = "extra/O Mentalista - 2ª Temporada Completa (720p) BLUDV.COM";
    private const string Ep = Pasta + "/O.Mentalista.S02E17.720p.WEB-DL.x264.Dublado-WWW.BLUDV.COM.mkv";
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    private static FaixaAudio Por(int i) => new(i, "ac3", "por", Default: i == 1, Canais: 2);
    private static FaixaAudio Eng(int i) => new(i, "ac3", "eng", Default: false, Canais: 2);

    // ─── a decisão, pura ─────────────────────────────────────────────────────────────

    [Fact]
    public void Serie_em_ingles_escolhe_a_faixa_eng_mesmo_com_por_default()
    {
        Assert.Equal(2, EscolhaAudio.Escolher([Por(1), Eng(2)], idiomaDaSerie: "eng"));
    }

    [Fact]
    public void Serie_pede_eng_e_o_arquivo_nao_tem_cai_no_portugues()
    {
        Assert.Equal(1, EscolhaAudio.Escolher([Por(1), new FaixaAudio(2, "ac3", "spa", false, 2)], idiomaDaSerie: "eng"));
    }

    [Fact]
    public void Escolha_manual_do_episodio_vence_a_da_serie()
    {
        Assert.Equal(1, EscolhaAudio.Escolher([Por(1), Eng(2)], preferenciaManual: 1, idiomaDaSerie: "eng"));
    }

    [Fact]
    public void Manual_que_nao_existe_no_arquivo_nao_atrapalha_a_serie()
    {
        Assert.Equal(2, EscolhaAudio.Escolher([Por(1), Eng(2)], preferenciaManual: 9, idiomaDaSerie: "eng"));
    }

    [Fact]
    public void Sem_preferencia_da_serie_continua_a_regra_de_sempre()
    {
        Assert.Equal(1, EscolhaAudio.Escolher([Por(1), Eng(2)], idiomaDaSerie: null));
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("ENG", "eng")]
    [InlineData("pt", "por")]
    [InlineData("pob", "por")]
    [InlineData("pt-BR", "por")]
    [InlineData("klingon", null)]
    [InlineData(null, null)]
    public void Normaliza_o_codigo_do_idioma(string? entrada, string? esperado) =>
        Assert.Equal(esperado, EscolhaAudio.NormalizarIdioma(entrada));

    [Fact]
    public void Idioma_da_serie_aceita_codigo_curto_e_a_faixa_do_arquivo_tambem()
    {
        Assert.Equal(2, EscolhaAudio.Escolher([new FaixaAudio(1, "ac3", "pt", true, 2), new FaixaAudio(2, "ac3", "en", false, 2)], idiomaDaSerie: "eng"));
    }

    // ─── persistência ────────────────────────────────────────────────────────────────

    private async Task<(ScanFixture F, int Id)> Cenario(string? ffprobe = null, string? ffmpeg = null)
    {
        var f = new ScanFixture(ffprobe: ffprobe, ffmpeg: ffmpeg);
        _fixtures.Add(f);
        f.Arquivo(Ep);
        await f.ScanAsync();
        return (f, await f.IdDeAsync(Ep));
    }

    [Fact]
    public async Task Preferencia_da_serie_vale_pras_4_pastas_de_temporada_e_sobrevive_ao_restart()
    {
        var (f, id) = await Cenario();
        f.Arquivo("extra/O Mentalista - 6ª Temporada Completa (720p) BLUDV.COM/O.Mentalista.S06E01.mkv");
        await f.ScanAsync();
        var id6 = await f.IdDeAsync("extra/O Mentalista - 6ª Temporada Completa (720p) BLUDV.COM/O.Mentalista.S06E01.mkv");

        var chave = MediaNomeParser.ChaveSerie(Ep);
        Assert.True(f.Prefs.DefinirIdiomaDaSerie(chave, "en"));

        // "restart": outro serviço, memória vazia, mesmo banco
        var depois = new PreferenciasAudioService(f.Escopos);
        Assert.Equal("eng", depois.Resolver(id).IdiomaDaSerie);
        Assert.Equal("eng", depois.Resolver(id6).IdiomaDaSerie);
        Assert.Equal("eng", depois.IdiomaDaSerie(chave));
    }

    [Fact]
    public async Task Null_volta_ao_automatico_e_idioma_desconhecido_e_recusado()
    {
        var (f, id) = await Cenario();
        var chave = MediaNomeParser.ChaveSerie(Ep);
        f.Prefs.DefinirIdiomaDaSerie(chave, "eng");

        Assert.False(f.Prefs.DefinirIdiomaDaSerie(chave, "klingon"));
        Assert.Equal("eng", f.Prefs.IdiomaDaSerie(chave));   // recusado não mexe no que valia

        Assert.True(f.Prefs.DefinirIdiomaDaSerie(chave, null));
        Assert.Null(new PreferenciasAudioService(f.Escopos).Resolver(id).IdiomaDaSerie);
    }

    [Fact]
    public async Task Escolha_manual_do_episodio_sobrevive_ao_restart()
    {
        var (f, id) = await Cenario();
        f.Prefs.DefinirFaixaDoEpisodio(id, 2);
        Assert.Equal(2, new PreferenciasAudioService(f.Escopos).Resolver(id).Manual);
    }

    // ─── ponta a ponta: lista de /audios + legenda automática ────────────────────────

    private string FfprobeMentalista() => TestesComuns.ScriptFalso(_bin, "ffprobe-mentalista.sh", """
        echo '{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720},{"index":1,"codec_type":"audio","codec_name":"ac3","channels":2,"disposition":{"default":1},"tags":{"language":"por"}},{"index":2,"codec_type":"audio","codec_name":"ac3","channels":2,"tags":{"language":"eng"}}],"format":{"duration":"2580.0"}}'
        """);

    [Theory]
    [InlineData("O.Mentalista.S02E17.720p.WEB-DL.x264.Dublado-WWW.BLUDV.COM", "extra/O Mentalista - 2ª Temporada Completa (720p) BLUDV.COM")]
    [InlineData("O.Mentalista.S03E23-24.720p.WEB-DL.x264.Dublado-WWW.BLUDV.COM", "extra/O Mentalista - 3ª Temporada Completa (720p) BLUDV.COM")]
    [InlineData("O.Mentalista.S06E10.720p.WEB-DL.x264.Dublado-WWW.BLUDV.COM", "extra/O Mentalista - 6ª Temporada Completa (720p) BLUDV.COM")]
    public async Task Audio_ingles_liga_sozinho_a_legenda_por_srt_ao_lado_do_video(string nome, string pasta)
    {
        var relativo = $"{pasta}/{nome}.mkv";
        var f = new ScanFixture(ffprobe: FfprobeMentalista());
        _fixtures.Add(f);
        var video = f.Arquivo(relativo);
        File.WriteAllText(Path.ChangeExtension(video, ".por.srt"), "1\n00:00:01,000 --> 00:00:02,000\nOi\n");
        await f.ScanAsync();
        var id = await f.IdDeAsync(relativo);
        var transcode = f.Servico<HlsTranscodeService>();
        var legendas = f.Servico<SubtitleService>();

        // o .por.srt aparece e é lido como Português
        var faixas = await legendas.ListarAsync(video, CancellationToken.None);
        var srt = Assert.Single(faixas);
        Assert.Equal("por", srt.Idioma);
        Assert.Equal("Português", srt.Titulo);

        // sem preferência: áudio por -> nada liga
        var audiosAuto = await transcode.ListarAudiosAsync(id, video, CancellationToken.None);
        Assert.Equal("por", audiosAuto.Single(a => a.Atual).Idioma);
        Assert.Null(SubtitleService.LegendaParaPreparar(audiosAuto.Single(a => a.Atual).Idioma, faixas));

        // série em inglês: áudio eng é o atual e a legenda PT vira a recomendada
        f.Prefs.DefinirIdiomaDaSerie(MediaNomeParser.ChaveSerie(relativo), "eng");
        var audiosEng = await transcode.ListarAudiosAsync(id, video, CancellationToken.None);
        var atual = audiosEng.Single(a => a.Atual);
        Assert.Equal("eng", atual.Idioma);
        Assert.Equal(srt.Idx, SubtitleService.LegendaParaPreparar(atual.Idioma, faixas));
    }

    // ─── remux em cache feito com outra faixa ────────────────────────────────────────

    [Fact]
    public async Task Remux_pronto_com_outra_faixa_e_refeito_so_quando_abrem_o_episodio()
    {
        var (f, id) = await Cenario(ffprobe: FfprobeMentalista());
        var transcode = f.Servico<HlsTranscodeService>();
        var video = f.Arquivo(Ep);

        // remux antigo feito com a faixa 1 (português)
        Directory.CreateDirectory(f.CacheRemux);
        var remux = transcode.CaminhoRemux(id);
        File.WriteAllText(remux, "mp4");
        File.WriteAllText(remux + ".audio", "1");

        // a série vira inglês: NADA é apagado na hora…
        f.Prefs.DefinirIdiomaDaSerie(MediaNomeParser.ChaveSerie(Ep), "eng");
        Assert.True(File.Exists(remux));

        // …só quando o episódio é aberto
        await transcode.DescartarRemuxDeOutraFaixaAsync(id, video, CancellationToken.None);
        Assert.False(File.Exists(remux));
    }

    [Fact]
    public async Task Remux_pronto_com_a_faixa_certa_ou_sem_anotacao_fica_como_esta()
    {
        var (f, id) = await Cenario(ffprobe: FfprobeMentalista());
        var transcode = f.Servico<HlsTranscodeService>();
        var video = f.Arquivo(Ep);
        Directory.CreateDirectory(f.CacheRemux);
        var remux = transcode.CaminhoRemux(id);
        File.WriteAllText(remux, "mp4");

        f.Prefs.DefinirIdiomaDaSerie(MediaNomeParser.ChaveSerie(Ep), "eng");
        await transcode.DescartarRemuxDeOutraFaixaAsync(id, video, CancellationToken.None);
        Assert.True(File.Exists(remux));   // cache antigo, sem anotação: não arrisca

        File.WriteAllText(remux + ".audio", "2");   // já é a eng
        await transcode.DescartarRemuxDeOutraFaixaAsync(id, video, CancellationToken.None);
        Assert.True(File.Exists(remux));
    }
}
