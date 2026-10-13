using FilmesApi.Models;
using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// "Só uso legenda quando o áudio não é em português": nesse caso a melhor legenda em português
/// já fica extraída no cache quando o filme abre. A mesma ordem decide qual faixa o botão CC do
/// celular / o vermelho da TV liga primeiro. Casos reais: Severance (a 1ª faixa é "FORÇADA
/// PT-BR" sem a flag forced) e Attack on Titan (9 faixas, português é a primeira).
/// </summary>
public class LegendaProntaTests
{
    private static LegendaInfo F(int idx, string? idioma, string? titulo = null, bool forced = false, bool conv = true) =>
        new(idx, "ass", idioma, titulo, forced, false, conv);

    // ─── Relevância / ordem ──────────────────────────────────────────────

    [Fact]
    public void Portugues_completa_vem_antes_da_forcada_mesmo_sem_a_flag_forced()
    {
        // Severance: 'FORÇADA PT-BR' está em primeiro no arquivo, mas é só fala estrangeira.
        var faixas = new List<LegendaInfo>
        {
            F(0, "por", "FORÇADA PT-BR"), F(1, "por", "COMPLETA PT-BR"), F(2, "por", "PT-BR"),
        };
        Assert.Equal([1, 2, 0], SubtitleService.OrdenarPorRelevancia(faixas).Select(x => x.Idx));
    }

    [Fact]
    public void Flag_forced_tambem_vale()
    {
        var faixas = new List<LegendaInfo> { F(0, "por", "Brazil", forced: true), F(1, "por", "Brazil") };
        Assert.Equal(1, SubtitleService.OrdenarPorRelevancia(faixas)[0].Idx);
    }

    [Fact]
    public void Sdh_fica_depois_da_portugues_completa()
    {
        var faixas = new List<LegendaInfo> { F(0, "por", "PT-BR SDH"), F(1, "por", "PT-BR") };
        Assert.Equal([1, 0], SubtitleService.OrdenarPorRelevancia(faixas).Select(x => x.Idx));
    }

    [Fact]
    public void Ordem_geral_portugues_sem_idioma_outro_idioma_forcada_bitmap()
    {
        var faixas = new List<LegendaInfo>
        {
            F(0, "eng", "English"),
            F(1, null, "Legenda"),
            F(2, "por", "Portuguese", conv: false),   // bitmap: não converte
            F(3, "por", "Portuguese"),
            F(4, "spa", "Español", forced: true),
        };
        Assert.Equal([3, 1, 0, 4, 2], SubtitleService.OrdenarPorRelevancia(faixas).Select(x => x.Idx));
    }

    [Fact]
    public void Ordenar_preserva_idx_e_a_ordem_original_entre_iguais()
    {
        // Attack on Titan: português primeiro, o resto (ara, ger, eng...) segue como veio.
        var faixas = new List<LegendaInfo>
        {
            F(0, "por", "Brazilian"), F(1, "ara", "Saudi Arabia"), F(2, "ger"), F(3, "eng"), F(4, "spa", "Latin American"),
        };
        var r = SubtitleService.OrdenarPorRelevancia(faixas);
        Assert.Equal([0, 1, 2, 3, 4], r.Select(x => x.Idx));
        Assert.Equal("Brazilian", r[0].Titulo);
    }

    // ─── Quando deixar pronta ────────────────────────────────────────────

    [Theory]
    [InlineData("eng")]
    [InlineData("jpn")]
    [InlineData("ENG")]
    public void Audio_que_nao_e_portugues_prepara_a_melhor_legenda_em_portugues(string audio)
    {
        var faixas = SubtitleService.OrdenarPorRelevancia(
            [F(0, "por", "FORÇADA"), F(1, "eng", "English"), F(2, "por", "PT-BR")]);
        Assert.Equal(2, SubtitleService.LegendaParaPreparar(audio, faixas));
    }

    [Theory]
    [InlineData("por")]
    [InlineData("pt")]
    [InlineData("pob")]
    [InlineData("POR")]
    public void Audio_em_portugues_nao_prepara_nada(string audio)
    {
        var faixas = SubtitleService.OrdenarPorRelevancia([F(0, "por", "PT-BR")]);
        Assert.Null(SubtitleService.LegendaParaPreparar(audio, faixas));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("und")]
    [InlineData("unk")]
    [InlineData("UND")]
    public void Audio_de_idioma_desconhecido_nao_prepara_nada(string? audio)
    {
        var faixas = SubtitleService.OrdenarPorRelevancia([F(0, "por", "PT-BR")]);
        Assert.Null(SubtitleService.LegendaParaPreparar(audio, faixas));
    }

    [Fact]
    public void So_legenda_em_ingles_nao_e_preparada_pra_audio_em_ingles()
    {
        var faixas = SubtitleService.OrdenarPorRelevancia([F(0, "eng", "English"), F(1, "fra", "Français")]);
        Assert.Null(SubtitleService.LegendaParaPreparar("eng", faixas));
    }

    [Fact]
    public void Legenda_externa_sem_idioma_e_preparada_pra_audio_estrangeiro()
    {
        // .srt solto de release brasileira costuma não dizer o idioma.
        var faixas = SubtitleService.OrdenarPorRelevancia([F(0, null, "Legenda")]);
        Assert.Equal(0, SubtitleService.LegendaParaPreparar("eng", faixas));
    }

    [Fact]
    public void So_forcada_ou_bitmap_nao_e_preparada()
    {
        Assert.Null(SubtitleService.LegendaParaPreparar("eng",
            SubtitleService.OrdenarPorRelevancia([F(0, "por", "Forced", forced: true)])));
        Assert.Null(SubtitleService.LegendaParaPreparar("eng",
            SubtitleService.OrdenarPorRelevancia([F(0, "por", "PGS", conv: false)])));
    }

    [Fact]
    public void Sem_legendas_nao_prepara_nada() =>
        Assert.Null(SubtitleService.LegendaParaPreparar("eng", []));

    [Theory]
    [InlineData("por", true)]
    [InlineData("PT", true)]
    [InlineData("pob", true)]
    [InlineData("eng", false)]
    [InlineData(null, false)]
    public void EhPortugues_reconhece(string? idioma, bool esperado) =>
        Assert.Equal(esperado, EscolhaAudio.EhPortugues(idioma));

    // ─── Despejo do cache de remux ───────────────────────────────────────
    // Regressão: ler FileInfo.Length depois de FileInfo.Delete() lançava FileNotFoundException,
    // o que abortava a limpeza (e logava warning) logo na 1ª remoção de cada rodada.

    private static string Mp4(string pasta, string nome, int bytes, TimeSpan idade)
    {
        var caminho = Path.Combine(pasta, nome);
        File.WriteAllBytes(caminho, new byte[bytes]);
        File.SetLastWriteTimeUtc(caminho, DateTime.UtcNow - idade);
        return caminho;
    }

    [Fact]
    public void Despejo_apaga_os_mais_antigos_ate_cair_a_90_por_cento_do_teto()
    {
        var pasta = TestesComuns.CriarPastaTemp();
        try
        {
            var a = Mp4(pasta, "a.mp4", 1000, TimeSpan.FromHours(5));
            var b = Mp4(pasta, "b.mp4", 1000, TimeSpan.FromHours(4));
            var c = Mp4(pasta, "c.mp4", 1000, TimeSpan.FromHours(3));

            // total 3000 > teto 2000; alvo 1800 → precisa apagar 2 (a e b) pra chegar em 1000.
            var despejados = HlsTranscodeService.DespejarExcedenteRemux(pasta, 2000, DateTime.UtcNow);

            Assert.Equal(["a.mp4", "b.mp4"], despejados.Select(x => x.Nome));
            Assert.All(despejados, x => Assert.Equal(1000, x.Bytes));
            Assert.False(File.Exists(a));
            Assert.False(File.Exists(b));
            Assert.True(File.Exists(c));
        }
        finally { Directory.Delete(pasta, recursive: true); }
    }

    [Fact]
    public void Despejo_nao_toca_no_que_foi_acessado_ha_menos_de_30_minutos()
    {
        var pasta = TestesComuns.CriarPastaTemp();
        try
        {
            var velho = Mp4(pasta, "velho.mp4", 1000, TimeSpan.FromHours(2));
            var recente = Mp4(pasta, "recente.mp4", 5000, TimeSpan.FromMinutes(5));

            var despejados = HlsTranscodeService.DespejarExcedenteRemux(pasta, 2000, DateTime.UtcNow);

            Assert.Equal(["velho.mp4"], despejados.Select(x => x.Nome));
            Assert.False(File.Exists(velho));
            Assert.True(File.Exists(recente));   // pode estar tocando, mesmo acima do teto
        }
        finally { Directory.Delete(pasta, recursive: true); }
    }

    [Fact]
    public void Despejo_abaixo_do_teto_ou_pasta_inexistente_nao_faz_nada()
    {
        var pasta = TestesComuns.CriarPastaTemp();
        try
        {
            var a = Mp4(pasta, "a.mp4", 1000, TimeSpan.FromHours(5));
            Assert.Empty(HlsTranscodeService.DespejarExcedenteRemux(pasta, 5000, DateTime.UtcNow));
            Assert.True(File.Exists(a));
            Assert.Empty(HlsTranscodeService.DespejarExcedenteRemux(Path.Combine(pasta, "nao-existe"), 1, DateTime.UtcNow));
        }
        finally { Directory.Delete(pasta, recursive: true); }
    }
}
