using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão: um job morto por travamento (sem progresso) ou por ninguém mais assistir
/// (órfão) NÃO é falha do encoder de hardware — só um exitCode genuinamente ruim é. Sem essa
/// distinção, 3 travamentos por disco lento/placa quente desligam o rkmpp pelo resto do
/// processo (só um restart do container liga de novo). É fácil "simplificar" essa condição de
/// volta pra só <c>!orfao</c> sem perceber a consequência — daí o teste isolado.
/// </summary>
public class ContaComoFalhaDeEncoderTests
{
    [Fact]
    public void Exit_ruim_normal_conta_como_falha_de_encoder()
    {
        Assert.True(HlsTranscodeService.ContaComoFalhaDeEncoder(orfao: false, travouSemProgresso: false));
    }

    [Fact]
    public void Orfao_nao_conta_como_falha_de_encoder()
    {
        Assert.False(HlsTranscodeService.ContaComoFalhaDeEncoder(orfao: true, travouSemProgresso: false));
    }

    [Fact]
    public void Travamento_sem_progresso_nao_conta_como_falha_de_encoder()
    {
        Assert.False(HlsTranscodeService.ContaComoFalhaDeEncoder(orfao: false, travouSemProgresso: true));
    }
}
