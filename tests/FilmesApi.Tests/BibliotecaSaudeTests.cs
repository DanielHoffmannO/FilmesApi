using System.Text.Json;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Saúde da biblioteca: as regras puras (truncado, estrutura do container, leitura perto do fim, duplicatas, português, transcode, cache, resumo).
/// Nenhum teste aqui precisa de ffmpeg de verdade.</summary>
public class BibliotecaRegrasTests
{
    private const long MB = 1024 * 1024;

    // ─── truncado ────────────────────────────────────────────────────────────────────

    private static DadosDeTruncamento Dados(long bytes = 2000 * MB, double? duracao = 3600, bool video = true,
        ResultadoEstrutura estrutura = default, LeituraDoFim? fim = null, bool probeOk = true) =>
        new(probeOk, bytes, duracao, video, estrutura, fim);

    [Fact]
    public void Probe_que_falha_e_ilegivel()
    {
        var d = SaudeBiblioteca.DecidirTruncado(Dados(probeOk: false));
        Assert.Equal(VeredictoTruncamento.Ilegivel, d.Veredito);
    }

    [Fact]
    public void Cabecalho_que_declara_mais_do_que_o_arquivo_tem_e_truncado()
    {
        var d = SaudeBiblioteca.DecidirTruncado(Dados(bytes: 600 * MB,
            estrutura: new ResultadoEstrutura(EstadoEstrutura.Truncada, "Matroska", 1000 * MB)));
        Assert.Equal(VeredictoTruncamento.Truncado, d.Veredito);
        Assert.Contains("600 MB", d.Detalhe);
        Assert.Contains("1000 MB", d.Detalhe);
        Assert.Contains("60%", d.Detalhe);
    }

    [Fact]
    public void Nenhum_quadro_perto_do_fim_declarado_e_truncado()
    {
        var d = SaudeBiblioteca.DecidirTruncado(Dados(fim: new LeituraDoFim(true, 0, 0, 10)));
        Assert.Equal(VeredictoTruncamento.Truncado, d.Veredito);
        Assert.Contains("10 s", d.Detalhe);
        Assert.Contains("1h00min", d.Detalhe);
    }

    [Fact]
    public void Dados_que_acabam_bem_antes_do_fim_declarado_sao_truncado()
    {
        var d = SaudeBiblioteca.DecidirTruncado(Dados(fim: new LeituraDoFim(true, 40, 2.5, 10)));   // 2,5 s de 10 s esperados
        Assert.Equal(VeredictoTruncamento.Truncado, d.Veredito);
    }

    [Fact]
    public void Janela_lida_quase_inteira_esta_ok()
    {
        var d = SaudeBiblioteca.DecidirTruncado(Dados(estrutura: new ResultadoEstrutura(EstadoEstrutura.Integra, "Matroska", 2000 * MB),
            fim: new LeituraDoFim(true, 240, 9.98, 10)));
        Assert.Equal(VeredictoTruncamento.Ok, d.Veredito);
        Assert.Null(d.Detalhe);
    }

    [Fact]
    public void Leitura_inconclusiva_nunca_acusa()
    {
        var d = SaudeBiblioteca.DecidirTruncado(Dados(fim: new LeituraDoFim(false, 0, null, 10)));
        Assert.Equal(VeredictoTruncamento.Ok, d.Veredito);
    }

    [Fact]
    public void Taxa_de_bits_absurda_e_so_suspeita()
    {
        // 30 MB para 1 h = ~70 kbps
        var d = SaudeBiblioteca.DecidirTruncado(Dados(bytes: 30 * MB, duracao: 3600));
        Assert.Equal(VeredictoTruncamento.Suspeito, d.Veredito);
        Assert.Contains("kbps", d.Detalhe);
    }

    [Theory]
    [InlineData(2000, 3600, true)]    // 4,4 Mbps: normal
    [InlineData(100, 3600, true)]     // 233 kbps: baixo, mas existe (anime HEVC)
    [InlineData(1, 120, true)]        // curto demais pra julgar a taxa
    [InlineData(5, 3600, false)]      // sem vídeo não entra (áudio puro pode ser baixo)
    public void Taxa_de_bits_so_vale_com_video_e_duracao_suficiente(long mb, double duracao, bool video)
    {
        var d = SaudeBiblioteca.DecidirTruncado(Dados(bytes: mb * MB, duracao: duracao, video: video));
        Assert.Equal(VeredictoTruncamento.Ok, d.Veredito);
    }

    // ─── cabeçalho do container ──────────────────────────────────────────────────────

    private static Func<long, int, byte[]> Leitor(byte[] dados) =>
        (pos, n) => pos >= dados.Length ? [] : dados[(int)pos..(int)Math.Min(dados.Length, pos + n)];

    private static byte[] Mkv(ulong tamanhoDoSegmento, bool desconhecido = false)
    {
        var b = new List<byte> { 0x1A, 0x45, 0xDF, 0xA3, 0x9F };   // EBML, tamanho 31
        b.AddRange(new byte[31]);
        b.AddRange([0x18, 0x53, 0x80, 0x67]);                        // Segment
        b.Add(0x01);                                                  // tamanho em 8 bytes
        for (var i = 6; i >= 0; i--) b.Add(desconhecido ? (byte)0xFF : (byte)(tamanhoDoSegmento >> (8 * i)));
        b.AddRange(new byte[64]);
        return [.. b];
    }

    [Fact]
    public void Matroska_com_menos_bytes_que_o_segmento_declara_esta_truncado()
    {
        // o Segment começa no byte 48 e declara 1000 bytes: o arquivo inteiro teria 1048
        var cab = Mkv(1000);
        var truncado = SaudeBiblioteca.VerificarEstrutura(".mkv", 500, Leitor(cab));
        Assert.Equal(EstadoEstrutura.Truncada, truncado.Estado);
        Assert.Equal(1048, truncado.EsperadoBytes);
        Assert.Equal(EstadoEstrutura.Integra, SaudeBiblioteca.VerificarEstrutura(".mkv", 1048, Leitor(cab)).Estado);
        Assert.Equal(EstadoEstrutura.Integra, SaudeBiblioteca.VerificarEstrutura(".webm", 5000, Leitor(cab)).Estado);   // lixo no fim não é corte
    }

    [Fact]
    public void Matroska_de_gravacao_ao_vivo_com_tamanho_desconhecido_nao_e_julgado()
    {
        Assert.Equal(EstadoEstrutura.Desconhecida, SaudeBiblioteca.VerificarEstrutura(".mkv", 10, Leitor(Mkv(0, desconhecido: true))).Estado);
    }

    [Fact]
    public void Arquivo_que_nao_e_do_formato_da_extensao_nao_e_julgado()
    {
        var lixo = new byte[200];
        foreach (var ext in new[] { ".mkv", ".mp4", ".avi", ".wmv", ".flv", ".xyz" })
            Assert.Equal(EstadoEstrutura.Desconhecida, SaudeBiblioteca.VerificarEstrutura(ext, 200, Leitor(lixo)).Estado);
        Assert.Equal(EstadoEstrutura.Desconhecida, SaudeBiblioteca.VerificarEstrutura(".mkv", 0, Leitor([])).Estado);
    }

    private static byte[] Caixa(string tipo, int tamanho)
    {
        var b = new byte[tamanho];
        b[0] = (byte)(tamanho >> 24); b[1] = (byte)(tamanho >> 16); b[2] = (byte)(tamanho >> 8); b[3] = (byte)tamanho;
        for (var i = 0; i < 4; i++) b[4 + i] = (byte)tipo[i];
        return b;
    }

    [Fact]
    public void Mp4_cuja_ultima_caixa_passa_do_fim_do_arquivo_esta_truncado()
    {
        byte[] inteiro = [.. Caixa("ftyp", 24), .. Caixa("moov", 100), .. Caixa("mdat", 1000)];
        Assert.Equal(EstadoEstrutura.Integra, SaudeBiblioteca.VerificarEstrutura(".mp4", inteiro.Length, Leitor(inteiro)).Estado);

        var cortado = inteiro[..900];
        var r = SaudeBiblioteca.VerificarEstrutura(".MP4", cortado.Length, Leitor(cortado));
        Assert.Equal(EstadoEstrutura.Truncada, r.Estado);
        Assert.Equal(inteiro.Length, r.EsperadoBytes);
        Assert.Equal(EstadoEstrutura.Truncada, SaudeBiblioteca.VerificarEstrutura(".mov", cortado.Length, Leitor(cortado)).Estado);
    }

    [Fact]
    public void Texto_com_extensao_de_mp4_nao_e_julgado_so_porque_os_primeiros_bytes_parecem_uma_caixa()
    {
        var texto = System.Text.Encoding.ASCII.GetBytes("conteudo de um arquivo qualquer que nao e MP4");
        Assert.Equal(EstadoEstrutura.Desconhecida, SaudeBiblioteca.VerificarEstrutura(".mp4", texto.Length, Leitor(texto)).Estado);
    }

    [Fact]
    public void Mp4_com_lixo_depois_da_ultima_caixa_nao_e_julgado()
    {
        byte[] comLixo = [.. Caixa("ftyp", 24), .. Caixa("mdat", 100), .. new byte[40]];   // 40 zeros: caixa de tamanho 0 = "até o fim"
        Assert.Equal(EstadoEstrutura.Desconhecida, SaudeBiblioteca.VerificarEstrutura(".mp4", comLixo.Length, Leitor(comLixo)).Estado);
    }

    private static byte[] Avi(uint tamanhoRiff) =>
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', (byte)tamanhoRiff, (byte)(tamanhoRiff >> 8), (byte)(tamanhoRiff >> 16), (byte)(tamanhoRiff >> 24),
         (byte)'A', (byte)'V', (byte)'I', (byte)' ', .. new byte[20]];

    [Fact]
    public void Avi_com_menos_bytes_que_o_riff_declara_esta_truncado()
    {
        var cab = Avi(1000);
        var r = SaudeBiblioteca.VerificarEstrutura(".avi", 700, Leitor(cab));
        Assert.Equal(EstadoEstrutura.Truncada, r.Estado);
        Assert.Equal(1008, r.EsperadoBytes);
        Assert.Equal(EstadoEstrutura.Integra, SaudeBiblioteca.VerificarEstrutura(".avi", 1008, Leitor(cab)).Estado);
        Assert.Equal(EstadoEstrutura.Integra, SaudeBiblioteca.VerificarEstrutura(".avi", 5_000_000, Leitor(cab)).Estado);   // OpenDML: mais bytes que o 1º RIFF é normal
    }

    // ─── leitura perto do fim ────────────────────────────────────────────────────────

    [Fact]
    public void Argumentos_da_leitura_perto_do_fim_buscam_antes_de_abrir_e_nao_decodificam()
    {
        var args = SaudeBiblioteca.ArgsLeituraDoFim("/media/F.mkv", 3590.5, 9.5, temVideo: true);

        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("3590.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("/media/F.mkv", args[args.IndexOf("-i") + 1]);
        Assert.Equal("0:v:0", args[args.IndexOf("-map") + 1]);
        Assert.Equal("19", args[args.IndexOf("-t") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c") + 1]);
        Assert.Equal("-", args[^1]);

        var soAudio = SaudeBiblioteca.ArgsLeituraDoFim("a.mkv", 0, 10, temVideo: false);
        Assert.Equal("0:a:0", soAudio[soAudio.IndexOf("-map") + 1]);
    }

    [Fact]
    public void Janela_do_fim_nunca_comeca_antes_de_zero()
    {
        Assert.Equal((90.0, 10.0), SaudeBiblioteca.JanelaDoFim(100, 10));
        Assert.Equal((0.0, 6.0), SaudeBiblioteca.JanelaDoFim(6, 10));
    }

    [Fact]
    public void Saida_normal_do_ffmpeg_vira_leitura_com_quadros_e_tempo()
    {
        const string stderr = """
            Input #0, matroska,webm, from 'f.mkv':
              Duration: 00:45:00.00, start: 0.000000, bitrate: 1500 kb/s
            frame=   12 fps=0.0 q=-1.0 size=N/A time=00:00:00.48 bitrate=N/A speed=9.6x
            frame=  240 fps=0.0 q=-1.0 Lsize=N/A time=00:00:09.98 bitrate=N/A speed= 150x
            video:3956kB audio:0kB subtitle:0kB other streams:0kB global headers:0kB muxing overhead: unknown
            """;
        var l = SaudeBiblioteca.InterpretarLeituraDoFim(0, stderr, 10);

        Assert.True(l.Conclusiva);
        Assert.Equal(240, l.Frames);
        Assert.Equal(9.98, l.SegundosLidos!.Value, 2);
    }

    [Fact]
    public void Saida_vazia_do_ffmpeg_e_zero_quadros()
    {
        var vazia = SaudeBiblioteca.InterpretarLeituraDoFim(0, "Output file is empty, nothing was encoded (check -ss / -t / -frames parameters if used)\n", 10);
        var zero = SaudeBiblioteca.InterpretarLeituraDoFim(0, "frame=    0 fps=0.0 q=-1.0 Lsize=N/A time=N/A bitrate=N/A speed=N/A\nvideo:0kB audio:0kB subtitle:0kB\n", 10);
        var soResumo = SaudeBiblioteca.InterpretarLeituraDoFim(0, "video:0kB audio:0kB subtitle:0kB\n", 10);

        foreach (var l in new[] { vazia, zero, soResumo })
        {
            Assert.True(l.Conclusiva);
            Assert.Equal(0, l.Frames);
        }
    }

    [Fact]
    public void Audio_puro_sem_frame_usa_o_resumo_de_bytes()
    {
        var l = SaudeBiblioteca.InterpretarLeituraDoFim(0, "size=N/A time=00:00:09.50 bitrate=N/A\nvideo:0kB audio:150kB subtitle:0kB\n", 10);
        Assert.True(l.Conclusiva);
        Assert.Equal(1, l.Frames);
    }

    [Theory]
    [InlineData(-1, "processo excedeu o timeout de 00:01:00 e foi encerrado.")]    // ProcessRunner: timeout
    [InlineData(-1, "não foi possível iniciar o processo.")]
    [InlineData(1, "Invalid data found when processing input")]                     // ffmpeg falhou: não é prova de nada
    [InlineData(0, "")]
    [InlineData(0, "saída que ninguém conhece")]
    public void Timeout_falha_ou_saida_irreconhecivel_e_inconclusiva(int exit, string stderr) =>
        Assert.False(SaudeBiblioteca.InterpretarLeituraDoFim(exit, stderr, 10).Conclusiva);

    // ─── português e transcode ───────────────────────────────────────────────────────

    private static MediaInfo Info(string? codec = "h264", bool dezBits = false, FaixaAudio[]? audios = null, FaixaLegenda[]? legendas = null) =>
        new(codec, 1920, 1080, dezBits, null, 3600, audios ?? [new FaixaAudio(1, "aac", "eng", true, 2)], legendas ?? []);

    [Fact]
    public void Sem_audio_nem_legenda_em_portugues_e_achado()
    {
        var d = SaudeBiblioteca.SemPortugues(Info(audios: [new FaixaAudio(1, "aac", "jpn", true, 2)],
            legendas: [new FaixaLegenda(0, "subrip", "eng", "English", false, false)]), []);
        Assert.NotNull(d);
        Assert.Contains("jpn", d);
    }

    [Theory]
    [InlineData("por")]
    [InlineData("pt")]
    [InlineData("pob")]
    public void Audio_em_portugues_basta(string idioma) =>
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(audios: [new FaixaAudio(1, "aac", "eng", false, 2), new FaixaAudio(2, "aac", idioma, true, 2)]), []));

    [Fact]
    public void Legenda_embutida_em_portugues_basta_pelo_idioma_ou_pelo_titulo()
    {
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(legendas: [new FaixaLegenda(0, "subrip", "por", null, false, false)]), []));
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(legendas: [new FaixaLegenda(0, "subrip", "eng", "Português (Brasil)", false, false)]), []));
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(legendas: [new FaixaLegenda(0, "subrip", null, "PT-BR forçada", true, false)]), []));
    }

    [Fact]
    public void Legenda_externa_em_portugues_basta()
    {
        Assert.NotNull(SaudeBiblioteca.SemPortugues(Info(), ["eng"]));
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(), ["eng", "por"]));
    }

    [Fact]
    public void Faixa_sem_idioma_informado_conta_como_talvez_portugues()
    {
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(audios: [new FaixaAudio(1, "aac", null, true, 2)]), []));
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(audios: [new FaixaAudio(1, "aac", "und", true, 2)]), []));
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(), [null]));   // .srt solto sem idioma
    }

    [Fact]
    public void Arquivo_sem_nenhuma_faixa_de_audio_nao_e_julgado() =>
        Assert.Null(SaudeBiblioteca.SemPortugues(Info(audios: []), []));

    [Theory]
    [InlineData("h264", false, false)]
    [InlineData("vp9", false, false)]
    [InlineData("av1", false, false)]
    [InlineData("hevc", false, true)]
    [InlineData("mpeg4", false, true)]
    [InlineData("h264", true, true)]       // High 10: o navegador não decodifica
    public void Transcode_pesado_e_video_que_nao_toca_nem_copiado(string codec, bool dezBits, bool pesado)
    {
        var d = SaudeBiblioteca.TranscodePesado(Info(codec, dezBits));
        Assert.Equal(pesado, d is not null);
        if (pesado) Assert.Contains(codec, d);
    }

    [Fact]
    public void Sem_video_nao_e_transcode_pesado() => Assert.Null(SaudeBiblioteca.TranscodePesado(Info(codec: null)));

    [Fact]
    public void Detalhe_do_transcode_pesado_diz_10_bit_e_resolucao()
    {
        var d = SaudeBiblioteca.TranscodePesado(Info("hevc", dezBits: true));
        Assert.Contains("10-bit", d);
        Assert.Contains("1920x1080", d);
    }

    // ─── cache ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Chave_de_cache_muda_com_caminho_tamanho_ou_mtime()
    {
        var mtime = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var chave = SaudeBiblioteca.ChaveDeCache("Filmes/A.mkv", 100, mtime);

        Assert.Equal(chave, SaudeBiblioteca.ChaveDeCache("Filmes/A.mkv", 100, mtime));
        Assert.NotEqual(chave, SaudeBiblioteca.ChaveDeCache("Filmes/B.mkv", 100, mtime));
        Assert.NotEqual(chave, SaudeBiblioteca.ChaveDeCache("Filmes/A.mkv", 101, mtime));
        Assert.NotEqual(chave, SaudeBiblioteca.ChaveDeCache("Filmes/A.mkv", 100, mtime.AddSeconds(1)));
    }

    [Fact]
    public void Resultado_completo_vale_sempre_e_o_incompleto_so_por_um_tempo()
    {
        var agora = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var completo = new ResultadoArquivoSaude("a", agora.AddDays(-100), true, []);
        var incompletoNovo = new ResultadoArquivoSaude("a", agora.AddHours(-2), false, []);
        var incompletoVelho = new ResultadoArquivoSaude("a", agora.AddHours(-21), false, []);

        Assert.True(SaudeBiblioteca.EntradaVale(completo, agora));
        Assert.True(SaudeBiblioteca.EntradaVale(incompletoNovo, agora));
        Assert.False(SaudeBiblioteca.EntradaVale(incompletoVelho, agora));
    }

    [Fact]
    public void Poda_tira_o_que_saiu_do_catalogo_ou_foi_trocado_e_guarda_o_que_a_rodada_nao_viu()
    {
        var t = DateTime.UtcNow;
        ResultadoArquivoSaude E(string a) => new(a, t, true, []);
        var entradas = new Dictionary<string, ResultadoArquivoSaude>
        {
            ["A|1|1"] = E("A"),      // chave atual: fica
            ["B|1|1"] = E("B"),      // B foi trocado (chave atual é outra): sai
            ["C|1|1"] = E("C"),      // C não está mais no catálogo: sai
            ["D|1|1"] = E("D"),      // D está no catálogo mas a rodada não chegou nele (ou sumiu do disco): fica
        };

        var podadas = SaudeBiblioteca.PodarEntradas(entradas, new HashSet<string> { "A", "B", "D" },
            new Dictionary<string, string> { ["A"] = "A|1|1", ["B"] = "B|2|2" });

        Assert.Equal(2, podadas);
        Assert.Equal(["A|1|1", "D|1|1"], entradas.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Rodada_so_e_devida_depois_do_intervalo_da_ultima_completa()
    {
        var agora = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var dia = TimeSpan.FromHours(24);

        Assert.True(SaudeBiblioteca.Devida(null, agora, dia));
        Assert.False(SaudeBiblioteca.Devida(agora.AddHours(-23), agora, dia));
        Assert.True(SaudeBiblioteca.Devida(agora.AddHours(-24), agora, dia));
    }

    // ─── agrupamento e resumo ────────────────────────────────────────────────────────

    private static ItemSaude Item(int id, string tipo, string sev = SeveridadeSaude.Info, string? grupo = null) =>
        new(id, $"T{id}", $"Filmes/{id}.mkv", tipo, sev, "x", grupo);

    [Fact]
    public void Resumo_conta_arquivos_por_tipo_e_duplicado_conta_grupos()
    {
        var itens = new[]
        {
            Item(1, TipoSaude.Truncado, SeveridadeSaude.Ruim), Item(2, TipoSaude.Truncado, SeveridadeSaude.Ruim), Item(3, TipoSaude.Truncado, SeveridadeSaude.Atencao),
            Item(4, TipoSaude.Duplicado, grupo: "g1"), Item(5, TipoSaude.Duplicado, grupo: "g1"), Item(6, TipoSaude.Duplicado, grupo: "g2"), Item(7, TipoSaude.Duplicado, grupo: "g2"),
            Item(8, TipoSaude.SemPortugues), Item(9, TipoSaude.TranscodePesado), Item(10, TipoSaude.Ilegivel, SeveridadeSaude.Ruim),
        };

        var resumo = SaudeBiblioteca.Resumir(itens);

        Assert.Equal(3, resumo[TipoSaude.Truncado]);
        Assert.Equal(2, resumo[TipoSaude.Duplicado]);
        Assert.Equal(1, resumo[TipoSaude.SemPortugues]);
        Assert.Equal(1, resumo[TipoSaude.TranscodePesado]);
        Assert.Equal(1, resumo[TipoSaude.Ilegivel]);
    }

    [Fact]
    public void Resumo_vazio_tem_os_quatro_tipos_do_contrato_com_zero()
    {
        var resumo = SaudeBiblioteca.Resumir([]);
        Assert.Equal(["truncado", "duplicado", "sem_portugues", "transcode_pesado", "ilegivel"], resumo.Keys);
        Assert.All(resumo.Values, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Texto_do_resumo_so_cita_o_que_tem()
    {
        Assert.Equal("3 truncado(s), 2 duplicado(s)", SaudeBiblioteca.ResumoEmTexto(
            new Dictionary<string, int> { ["truncado"] = 3, ["duplicado"] = 2, ["sem_portugues"] = 0 }));
        Assert.Equal("1 ilegível(is), 40 sem português, 7 transcode pesado", SaudeBiblioteca.ResumoEmTexto(
            new Dictionary<string, int> { ["sem_portugues"] = 40, ["transcode_pesado"] = 7, ["ilegivel"] = 1 }));
        Assert.Equal("nenhum problema", SaudeBiblioteca.ResumoEmTexto(SaudeBiblioteca.Resumir([])));
    }

    [Fact]
    public void Relatorio_ordena_ruim_antes_de_atencao_antes_de_info()
    {
        var r = SaudeBiblioteca.MontarRelatorio(DateTime.UtcNow, 10, 9, [
            Item(1, TipoSaude.SemPortugues), Item(2, TipoSaude.Duplicado, SeveridadeSaude.Atencao, "g"), Item(3, TipoSaude.Truncado, SeveridadeSaude.Ruim),
            Item(4, TipoSaude.TranscodePesado), Item(5, TipoSaude.Ilegivel, SeveridadeSaude.Ruim),
        ], null);

        Assert.Equal([3, 5, 2, 1, 4], r.Itens.Select(i => i.FilmeId));
        Assert.Equal(10, r.Total);
        Assert.Equal(9, r.Analisados);
    }

    // ─── do catálogo ao relatório ────────────────────────────────────────────────────

    private static List<FilmeResponse> Catalogo(params string[] caminhos) => new ClassificacaoCache().Classificar(
        caminhos.Select((c, i) => new FilmeResponse(i + 1, Path.GetFileNameWithoutExtension(c), null, c, false, DateTime.UtcNow,
            null, null, null, null, null, false, false, null, null, null, null, "", "Sem pasta")).ToList());

    [Fact]
    public void Achados_guardados_viram_itens_com_o_id_e_o_titulo_do_catalogo()
    {
        var catalogo = Catalogo("Filmes/Matrix.1999.mkv", "Series/Hora de Aventura/Hora.de.Aventura.S01E02.mkv");
        var entradas = new Dictionary<string, ResultadoArquivoSaude>
        {
            ["Series/Hora de Aventura/Hora.de.Aventura.S01E02.mkv"] = new("Series/Hora de Aventura/Hora.de.Aventura.S01E02.mkv", DateTime.UtcNow, true,
                [new AchadoSaude(TipoSaude.Truncado, SeveridadeSaude.Ruim, "cortado")]),
        };

        var itens = SaudeBiblioteca.ItensDosAchados(catalogo, c => entradas.GetValueOrDefault(c));

        var item = Assert.Single(itens);
        Assert.Equal(2, item.FilmeId);
        Assert.Equal("Hora de Aventura S01E02", item.Titulo);
        Assert.Equal(TipoSaude.Truncado, item.Tipo);
    }

    [Fact]
    public void Barbie_em_avi_e_mp4_na_mesma_pasta_e_um_grupo_com_o_mp4_de_candidato()
    {
        var catalogo = Catalogo("Filmes/Barbie (2023)/Barbie.avi", "Filmes/Barbie (2023)/Barbie.mp4", "Filmes/Oppenheimer.mkv");
        var tamanhos = new Dictionary<string, long?> { ["Filmes/Barbie (2023)/Barbie.avi"] = 700 * MB, ["Filmes/Barbie (2023)/Barbie.mp4"] = 900 * MB };

        var grupos = SaudeBiblioteca.DuplicatasDoCatalogo(catalogo, new HashSet<int>(), c => tamanhos.GetValueOrDefault(c));
        var itens = SaudeBiblioteca.ItensDeDuplicatas(grupos);

        var grupo = Assert.Single(grupos);
        Assert.StartsWith("nome:", grupo.Chave);
        Assert.Equal(2, itens.Count);
        Assert.All(itens, i => { Assert.Equal(TipoSaude.Duplicado, i.Tipo); Assert.Equal(grupo.Chave, i.Grupo); });
        var candidato = Assert.Single(itens, i => i.CandidatoManter == true);
        Assert.EndsWith("Barbie.mp4", candidato.Arquivo);
        Assert.Contains("Barbie.mp4", itens.Single(i => i.CandidatoManter == false).Detalhe);   // o outro aponta pro candidato
        Assert.Equal(1, SaudeBiblioteca.Resumir(itens)[TipoSaude.Duplicado]);
    }

    [Fact]
    public void Mesmo_nome_em_pastas_diferentes_nao_e_duplicata_de_nome()
    {
        var catalogo = Catalogo("Filmes/Alien/Alien.mp4", "Filmes/Alien (Versao do diretor)/Alien.avi");
        Assert.Empty(SaudeBiblioteca.DuplicatasDoCatalogo(catalogo, new HashSet<int>(), _ => null));
    }

    [Fact]
    public void Mesmo_episodio_em_dois_arquivos_da_mesma_serie_e_duplicata()
    {
        var catalogo = Catalogo(
            "Series/Hora de Aventura/Hora.de.Aventura.S01E01.720p.mkv",
            "Series/Hora de Aventura/Hora.de.Aventura.S01E01.1080p.mkv",
            "Series/Hora de Aventura/Hora.de.Aventura.S01E02.720p.mkv");
        var tamanhos = new Dictionary<string, long?>
        {
            ["Series/Hora de Aventura/Hora.de.Aventura.S01E01.720p.mkv"] = 300 * MB,
            ["Series/Hora de Aventura/Hora.de.Aventura.S01E01.1080p.mkv"] = 900 * MB,
        };

        var grupos = SaudeBiblioteca.DuplicatasDoCatalogo(catalogo, new HashSet<int>(), c => tamanhos.GetValueOrDefault(c));
        var itens = SaudeBiblioteca.ItensDeDuplicatas(grupos);

        var grupo = Assert.Single(grupos);
        Assert.StartsWith("ep:", grupo.Chave);
        Assert.Contains("T01E01", grupo.Motivo);
        Assert.EndsWith("1080p.mkv", Assert.Single(itens, i => i.CandidatoManter == true).Arquivo);   // empate de formato: o maior
    }

    [Fact]
    public void Par_avi_e_mp4_do_mesmo_episodio_nao_conta_duas_vezes()
    {
        var catalogo = Catalogo("Series/Show/Show.S02E03.avi", "Series/Show/Show.S02E03.mp4");

        var grupos = SaudeBiblioteca.DuplicatasDoCatalogo(catalogo, new HashSet<int>(), _ => null);

        Assert.Single(grupos);
        Assert.StartsWith("nome:", grupos[0].Chave);
    }

    [Fact]
    public void Arquivo_truncado_nunca_e_o_candidato_a_manter()
    {
        var catalogo = Catalogo("Filmes/Guerra de Canudos/Guerra de Canudos.avi", "Filmes/Guerra de Canudos/Guerra de Canudos.mp4");
        var idDoMp4 = catalogo.Single(f => f.ArquivoPath!.EndsWith(".mp4")).Id;

        var itens = SaudeBiblioteca.ItensDeDuplicatas(SaudeBiblioteca.DuplicatasDoCatalogo(catalogo, new HashSet<int> { idDoMp4 }, _ => null));

        var candidato = Assert.Single(itens, i => i.CandidatoManter == true);
        Assert.EndsWith(".avi", candidato.Arquivo);
        Assert.Contains("truncados", candidato.Detalhe);
    }

    [Theory]
    [InlineData(".mp4", ".mkv")]
    [InlineData(".mkv", ".avi")]
    [InlineData(".webm", ".avi")]
    public void Formato_que_toca_mais_facil_ganha_o_desempate(string melhor, string pior)
    {
        var a = new ArquivoParaDuplicata(1, "x", "p/x" + pior, 5000, false);
        var b = new ArquivoParaDuplicata(2, "x", "p/x" + melhor, 10, false);
        Assert.Equal(1, SaudeBiblioteca.EscolherCandidato([a, b]));
        Assert.Equal(0, SaudeBiblioteca.EscolherCandidato([b, a]));
    }

    // ─── a forma do JSON e a proteção da rota ────────────────────────────────────────

    [Fact]
    public void Json_do_relatorio_tem_o_formato_do_contrato()
    {
        var r = SaudeBiblioteca.MontarRelatorio(new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc), 3, 2, [
            Item(1, TipoSaude.Truncado, SeveridadeSaude.Ruim), Item(2, TipoSaude.Duplicado, SeveridadeSaude.Atencao, "g1") with { CandidatoManter = true },
        ], null);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var raiz = doc.RootElement;

        Assert.Equal(3, raiz.GetProperty("total").GetInt32());
        Assert.Equal(2, raiz.GetProperty("analisados").GetInt32());
        Assert.True(raiz.TryGetProperty("geradoEm", out _));
        var resumo = raiz.GetProperty("resumo");
        Assert.Equal(1, resumo.GetProperty("truncado").GetInt32());
        Assert.Equal(1, resumo.GetProperty("duplicado").GetInt32());
        Assert.Equal(0, resumo.GetProperty("sem_portugues").GetInt32());
        Assert.Equal(0, resumo.GetProperty("transcode_pesado").GetInt32());
        var itens = raiz.GetProperty("itens");
        var primeiro = itens[0];
        foreach (var campo in new[] { "filmeId", "titulo", "arquivo", "tipo", "severidade", "detalhe" })
            Assert.True(primeiro.TryGetProperty(campo, out _), campo);
        Assert.Equal("ruim", primeiro.GetProperty("severidade").GetString());
        Assert.False(primeiro.TryGetProperty("grupo", out _));               // só existe nas duplicatas
        Assert.Equal("g1", itens[1].GetProperty("grupo").GetString());
        Assert.True(itens[1].GetProperty("candidatoManter").GetBoolean());
    }

    [Theory]
    [InlineData("POST", "/api/diagnostico/biblioteca/verificar", true)]
    [InlineData("POST", "/API/DIAGNOSTICO/BIBLIOTECA/VERIFICAR", true)]
    [InlineData("POST", "/api/diagnostico/biblioteca/verificar/", true)]
    [InlineData("GET", "/api/diagnostico/biblioteca", false)]            // ler o relatório é aberto
    [InlineData("GET", "/api/diagnostico/biblioteca/verificar", false)]
    [InlineData("POST", "/api/diagnostico/biblioteca/verificarx", false)]
    public void So_o_post_de_verificar_pede_token(string metodo, string caminho, bool protegida) =>
        Assert.Equal(protegida, TokenAdmin.Protegida(metodo, new PathString(caminho)));
}

/// <summary>A rodada de ponta a ponta com ffprobe/ffmpeg FALSOS (scripts): o ffprobe responde pelo nome do arquivo e o ffmpeg imprime o stderr que o de verdade
/// imprimiria. Confere cache por arquivo, persistência em JSON, pausa com o servidor ocupado, cancelamento e o resumo da tarefa em /api/status.</summary>
public class BibliotecaServicoTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];
    private readonly string _contadorFfmpeg;

    public BibliotecaServicoTests() => _contadorFfmpeg = Path.Combine(_bin, "ffmpeg-chamadas.txt");

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    private const string Video = """{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720}""";

    private string FfprobeFalso() => TestesComuns.ScriptFalso(_bin, "ffprobe.sh", $$$"""
        for a; do ultimo="$a"; done
        case "$ultimo" in
          *ilegivel*) exit 1 ;;
          *dublado*) echo '{"streams":[{{{Video}}},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"por"}}],"format":{"duration":"120.0"}}' ;;
          *hevc*) echo '{"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":1920,"height":1080,"pix_fmt":"yuv420p10le"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"por"}}],"format":{"duration":"120.0"}}' ;;
          *) echo '{"streams":[{{{Video}}},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"}}],"format":{"duration":"120.0"}}' ;;
        esac
        """);

    private string FfmpegFalso() => TestesComuns.ScriptFalso(_bin, "ffmpeg.sh", $$$"""
        while [ $# -gt 0 ]; do [ "$1" = "-i" ] && arquivo="$2"; shift; done
        echo "$arquivo" >> "{{{_contadorFfmpeg}}}"
        case "$arquivo" in
          *cortado*)
            echo "Output file is empty, nothing was encoded (check -ss / -t / -frames parameters if used)" >&2
            echo "video:0kB audio:0kB subtitle:0kB" >&2 ;;
          *)
            echo "frame=  240 fps=0.0 q=-1.0 Lsize=N/A time=00:00:09.98 bitrate=N/A speed= 150x" >&2
            echo "video:3956kB audio:0kB subtitle:0kB" >&2 ;;
        esac
        """);

    private int ChamadasDoFfmpeg => File.Exists(_contadorFfmpeg) ? File.ReadAllLines(_contadorFfmpeg).Length : 0;

    private ScanFixture Cenario(params string[] arquivos)
    {
        var f = new ScanFixture(ffmpeg: FfmpegFalso(), ffprobe: FfprobeFalso());
        _fixtures.Add(f);
        foreach (var a in arquivos) f.Arquivo(a, "conteudo de " + a);
        f.ScanAsync().GetAwaiter().GetResult();
        return f;
    }

    private static string ArquivoDeEstado(ScanFixture f) => Path.Combine(f.Raiz, "estado", "saude.json");

    private static SaudeBibliotecaService Servico(ScanFixture f, RegistroDeTarefas? registro = null, Dictionary<string, string?>? extra = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaPath"] = f.Media,
            ["SaudeBibliotecaPath"] = ArquivoDeEstado(f),
            ["SaudeBibliotecaPausaSegundos"] = "0",
        }.Concat(extra ?? []).ToDictionary(kv => kv.Key, kv => kv.Value)).Build();
        return new SaudeBibliotecaService(f.Escopos, f.Servico<FfmpegOptions>(), f.Servico<MediaProbeService>(), f.Servico<HlsTranscodeService>(),
            f.Servico<ThermalService>(), config, NullLogger<SaudeBibliotecaService>.Instance, registro);
    }

    [Fact]
    public async Task Rodada_acha_truncado_ilegivel_duplicata_sem_portugues_e_transcode_pesado()
    {
        var f = Cenario(
            "Filmes/Matrix.dublado.mkv",              // tudo certo: áudio em português, vídeo H.264
            "Filmes/Perdido.cortado.mkv",             // o ffmpeg não acha dados perto do fim
            "Filmes/Quebrado.ilegivel.mkv",           // o ffprobe não lê
            "Filmes/Antigo.hevc.mkv",                 // HEVC 10-bit: transcode pesado
            "Filmes/Barbie/Barbie.avi", "Filmes/Barbie/Barbie.mp4");   // áudio em inglês, sem legenda: sem português
        var registro = new RegistroDeTarefas();
        var servico = Servico(f, registro);

        var rodada = await servico.ExecutarRodadaAsync(manual: true);
        var r = servico.ObterRelatorio();

        Assert.True(rodada.Completa);
        Assert.Equal(6, r.Total);
        Assert.Equal(6, r.Analisados);
        Assert.Equal(1, r.Resumo[TipoSaude.Truncado]);
        Assert.Equal(1, r.Resumo[TipoSaude.Ilegivel]);
        Assert.Equal(1, r.Resumo[TipoSaude.Duplicado]);
        Assert.Equal(1, r.Resumo[TipoSaude.TranscodePesado]);
        Assert.Equal(3, r.Resumo[TipoSaude.SemPortugues]);   // Perdido.cortado e os dois Barbie (áudio em inglês, sem legenda)
        var truncado = Assert.Single(r.Itens, i => i.Tipo == TipoSaude.Truncado);
        Assert.Equal(SeveridadeSaude.Ruim, truncado.Severidade);
        Assert.EndsWith("Perdido.cortado.mkv", truncado.Arquivo);
        Assert.Equal(2, r.Itens.Count(i => i.Tipo == TipoSaude.Duplicado));
        Assert.EndsWith("Barbie.mp4", Assert.Single(r.Itens, i => i.CandidatoManter == true).Arquivo);

        var tarefa = Assert.Single(registro.Snapshot(), t => t.Nome == "saude-biblioteca");
        Assert.Equal("ok", tarefa.Estado);
        Assert.Contains("1 truncado(s)", tarefa.Resumo);
        Assert.Contains("1 duplicado(s)", tarefa.Resumo);
    }

    [Fact]
    public async Task Rodada_nao_mexe_nos_arquivos_de_midia()
    {
        var f = Cenario("Filmes/Perdido.cortado.mkv", "Filmes/Barbie/Barbie.avi", "Filmes/Barbie/Barbie.mp4");
        var antes = Directory.EnumerateFiles(f.Media, "*", SearchOption.AllDirectories).OrderBy(p => p)
            .Select(p => (p, File.ReadAllText(p), File.GetLastWriteTimeUtc(p))).ToList();

        await Servico(f).ExecutarRodadaAsync(manual: true);

        var depois = Directory.EnumerateFiles(f.Media, "*", SearchOption.AllDirectories).OrderBy(p => p)
            .Select(p => (p, File.ReadAllText(p), File.GetLastWriteTimeUtc(p))).ToList();
        Assert.Equal(antes, depois);
    }

    [Fact]
    public async Task Segunda_rodada_so_reanalisa_o_que_mudou()
    {
        var f = Cenario("Filmes/A.dublado.mkv", "Filmes/B.dublado.mkv");
        var servico = Servico(f);

        var primeira = await servico.ExecutarRodadaAsync(manual: true);
        var chamadas = ChamadasDoFfmpeg;
        var segunda = await servico.ExecutarRodadaAsync(manual: true);

        Assert.Equal(2, primeira.Analisados);
        Assert.Equal(0, segunda.Analisados);
        Assert.Equal(2, segunda.Reaproveitados);
        Assert.Equal(chamadas, ChamadasDoFfmpeg);

        File.AppendAllText(Path.Combine(f.Media, "Filmes/B.dublado.mkv"), " mais bytes");   // arquivo trocado: tamanho (e mtime) novos
        var terceira = await servico.ExecutarRodadaAsync(manual: true);

        Assert.Equal(1, terceira.Analisados);
        Assert.Equal(1, terceira.Reaproveitados);
        Assert.Equal(chamadas + 1, ChamadasDoFfmpeg);
    }

    [Fact]
    public async Task Resultado_fica_em_json_e_sobrevive_a_um_reinicio()
    {
        var f = Cenario("Filmes/A.dublado.mkv", "Filmes/Perdido.cortado.mkv");
        await Servico(f).ExecutarRodadaAsync(manual: true);
        Assert.True(File.Exists(ArquivoDeEstado(f)));
        var chamadas = ChamadasDoFfmpeg;

        var depois = Servico(f);   // "processo novo": só tem o arquivo
        var antesDeRodar = depois.ObterRelatorio();
        var rodada = await depois.ExecutarRodadaAsync(manual: true);

        Assert.NotNull(antesDeRodar.GeradoEm);
        Assert.Equal(1, antesDeRodar.Resumo[TipoSaude.Truncado]);
        Assert.Equal(0, rodada.Analisados);
        Assert.Equal(chamadas, ChamadasDoFfmpeg);
        Assert.False(File.Exists(ArquivoDeEstado(f) + ".tmp"));
    }

    [Fact]
    public async Task Json_de_estado_ilegivel_recomeca_do_zero_sem_derrubar()
    {
        var f = Cenario("Filmes/A.dublado.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(ArquivoDeEstado(f))!);
        File.WriteAllText(ArquivoDeEstado(f), "{ isto não é json");
        var servico = Servico(f);

        Assert.Null(servico.ObterRelatorio().GeradoEm);
        var rodada = await servico.ExecutarRodadaAsync(manual: true);

        Assert.Equal(1, rodada.Analisados);
    }

    [Fact]
    public async Task Com_o_servidor_ocupado_nada_e_analisado_e_a_rodada_fica_incompleta()
    {
        var f = Cenario("Filmes/A.dublado.mkv", "Filmes/B.dublado.mkv");
        var registro = new RegistroDeTarefas();
        var servico = Servico(f, registro);
        servico.MotivoDeEspera = () => "servidor ocupado (transcode/remux ativo)";

        var rodada = await servico.ExecutarRodadaAsync(manual: false);

        Assert.Equal(0, rodada.Analisados);
        Assert.False(rodada.Completa);
        Assert.Equal(0, ChamadasDoFfmpeg);
        Assert.Null(servico.ObterRelatorio().UltimaRodadaCompleta);   // continua devida: a próxima checagem retoma
        Assert.Contains("pausado: servidor ocupado", registro.Snapshot().Single(t => t.Nome == "saude-biblioteca").Resumo);

        servico.MotivoDeEspera = () => null;   // o servidor ficou livre
        var retomada = await servico.ExecutarRodadaAsync(manual: false);
        Assert.Equal(2, retomada.Analisados);
        Assert.True(retomada.Completa);
        Assert.NotNull(servico.ObterRelatorio().UltimaRodadaCompleta);
    }

    [Fact]
    public async Task Orcamento_de_tempo_para_a_rodada_automatica_mas_nao_a_manual()
    {
        var f = Cenario("Filmes/A.dublado.mkv", "Filmes/B.dublado.mkv");
        // orçamento de 1,2 s: o 1º arquivo cabe, mas o ffmpeg falso leva 2 s+ (o ProcessRunner confere a cada 2 s), então a rodada automática para antes do 2º
        var servico = Servico(f, extra: new() { ["SaudeBibliotecaOrcamentoMinutos"] = "0.02" });

        var automatica = await servico.ExecutarRodadaAsync(manual: false);
        Assert.Equal(1, automatica.Analisados);
        Assert.False(automatica.Completa);
        Assert.Contains("orçamento", automatica.Pausa);

        var manual = await servico.ExecutarRodadaAsync(manual: true);
        Assert.Equal(1, manual.Analisados);   // o que faltava
        Assert.True(manual.Completa);
    }

    [Fact]
    public async Task Cinco_leituras_seguidas_que_falham_param_a_rodada_sem_gravar_as_falhas()
    {
        var f = Cenario(Enumerable.Range(1, 6).Select(i => $"Filmes/F{i}.ilegivel.mkv").ToArray());   // disco caído: o ffprobe falha em todos
        var servico = Servico(f);

        var rodada = await servico.ExecutarRodadaAsync(manual: true);
        var r = servico.ObterRelatorio();

        Assert.Equal(SaudeBibliotecaService.LimiteDeFalhasSeguidas, rodada.Analisados);
        Assert.False(rodada.Completa);
        Assert.Contains("montagem caída", rodada.Pausa);
        Assert.Equal(0, r.Analisados);   // nenhuma dessas falhas foi guardada como "ilegível"
        Assert.Equal(0, r.Resumo[TipoSaude.Ilegivel]);
    }

    [Fact]
    public async Task Ilegivel_isolado_e_relatado_e_refeito_depois()
    {
        var f = Cenario("Filmes/A.dublado.mkv", "Filmes/Quebrado.ilegivel.mkv", "Filmes/B.dublado.mkv");
        var servico = Servico(f);

        await servico.ExecutarRodadaAsync(manual: true);
        var r = servico.ObterRelatorio();

        var item = Assert.Single(r.Itens, i => i.Tipo == TipoSaude.Ilegivel);
        Assert.Equal(SeveridadeSaude.Ruim, item.Severidade);
        var segunda = await servico.ExecutarRodadaAsync(manual: true);
        Assert.Equal(0, segunda.Analisados);   // incompleto, mas recente: só é refeito depois de 20 h
    }

    [Fact]
    public async Task Cancelar_interrompe_a_rodada()
    {
        var f = Cenario("Filmes/A.dublado.mkv");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Servico(f).ExecutarRodadaAsync(manual: true, cts.Token));
    }

    [Fact]
    public async Task Intervalo_zero_desliga_e_a_tarefa_aparece_desligada()
    {
        var f = Cenario();
        var registro = new RegistroDeTarefas();
        var servico = Servico(f, registro, new() { ["SaudeBibliotecaIntervaloHoras"] = "0" });

        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await servico.StopAsync(CancellationToken.None);

        Assert.Equal("desligada", registro.Snapshot().Single(t => t.Nome == "saude-biblioteca").Estado);
    }

    [Fact]
    public async Task Parar_o_servico_com_o_timer_ligado_termina_limpo()
    {
        var f = Cenario();
        var registro = new RegistroDeTarefas();
        var servico = Servico(f, registro, new() { ["SaudeBibliotecaAtrasoInicialSegundos"] = "3600" });

        await servico.StartAsync(CancellationToken.None);
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (registro.Snapshot().Count == 0 && DateTime.UtcNow < limite) await Task.Delay(20);   // o ExecuteAsync pode começar em outra thread
        await servico.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("aguardando", registro.Snapshot().Single(t => t.Nome == "saude-biblioteca").Estado);
    }

    [Fact]
    public async Task Disparar_comeca_em_segundo_plano_e_nao_deixa_duas_juntas()
    {
        var f = Cenario("Filmes/Quebrado.ilegivel.mkv");   // só falha de ffprobe: rápido
        var servico = Servico(f);

        servico.MotivoDeEspera = () => "ocupado";
        Assert.Equal(DisparoDeSaude.ServidorOcupado, servico.Disparar());
        Assert.False(servico.Rodando);

        // a 1ª consulta é a do próprio Disparar(); a 2ª (já dentro da rodada) segura até o teste soltar, pra a rodada não acabar antes das asserções
        using var portao = new ManualResetEventSlim();
        var consultas = 0;
        servico.MotivoDeEspera = () => { if (Interlocked.Increment(ref consultas) > 1) portao.Wait(TimeSpan.FromSeconds(10)); return null; };
        Assert.Equal(DisparoDeSaude.Iniciada, servico.Disparar());
        Assert.True(servico.Rodando);                                    // já aparece rodando ao voltar do POST
        Assert.Equal(DisparoDeSaude.JaRodando, servico.Disparar());
        Assert.True(servico.ObterRelatorio().Rodando);

        portao.Set();
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (servico.Rodando && DateTime.UtcNow < limite) await Task.Delay(50);
        Assert.False(servico.Rodando);
        Assert.NotNull(servico.ObterRelatorio().GeradoEm);
        Assert.Equal(1, servico.ObterRelatorio().Resumo[TipoSaude.Ilegivel]);
    }
}
