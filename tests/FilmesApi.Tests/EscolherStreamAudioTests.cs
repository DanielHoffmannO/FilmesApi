using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão da feature de "dublagem" no controle remoto: EscolherStreamAudio decide qual
/// faixa vai pro -map do HLS/remux, e agora também precisa respeitar uma preferência manual
/// (controle.html escolheu uma faixa) além da prioridade automática de sempre (idioma pt >
/// default > primeira).
/// </summary>
public class EscolherStreamAudioTests
{
    private static FaixaAudio Ingles(int index, bool def = false) => new(index, "aac", "eng", def, Canais: 2);
    private static FaixaAudio Portugues(int index, bool def = false) => new(index, "aac", "por", def, Canais: 2);

    [Fact]
    public void Sem_audio_devolve_null()
    {
        Assert.Null(HlsTranscodeService.EscolherStreamAudio([]));
    }

    [Fact]
    public void Sem_preferencia_prioriza_portugues_mesmo_nao_sendo_a_primeira_nem_default()
    {
        var audios = new[] { Ingles(1, def: true), Portugues(2) };
        Assert.Equal(2, HlsTranscodeService.EscolherStreamAudio(audios));
    }

    [Fact]
    public void Sem_preferencia_sem_portugues_usa_a_default()
    {
        var audios = new[] { Ingles(1), Ingles(2, def: true) };
        Assert.Equal(2, HlsTranscodeService.EscolherStreamAudio(audios));
    }

    [Fact]
    public void Sem_preferencia_sem_portugues_sem_default_usa_a_primeira()
    {
        var audios = new[] { Ingles(5), Ingles(7) };
        Assert.Equal(5, HlsTranscodeService.EscolherStreamAudio(audios));
    }

    [Fact]
    public void Preferencia_manual_valida_vence_a_prioridade_automatica()
    {
        // Preferência aponta pra faixa em inglês — mesmo havendo uma em português "melhor"
        // pela regra automática, o controle remoto escolheu essa de propósito.
        var audios = new[] { Portugues(1, def: true), Ingles(2) };
        Assert.Equal(2, HlsTranscodeService.EscolherStreamAudio(audios, preferenciaManual: 2));
    }

    [Fact]
    public void Preferencia_manual_que_nao_existe_mais_no_arquivo_cai_pra_automatica()
    {
        // Caso real: arquivo foi re-escaneado/trocado e o índice guardado não bate mais com
        // nenhuma faixa atual — não pode travar nem escolher nada fora do intervalo.
        var audios = new[] { Ingles(1), Portugues(2) };
        Assert.Equal(2, HlsTranscodeService.EscolherStreamAudio(audios, preferenciaManual: 99));
    }

    [Fact]
    public void Preferencia_manual_nula_se_comporta_como_sem_preferencia()
    {
        var audios = new[] { Ingles(1, def: true), Portugues(2) };
        Assert.Equal(
            HlsTranscodeService.EscolherStreamAudio(audios),
            HlsTranscodeService.EscolherStreamAudio(audios, preferenciaManual: null));
    }
}
