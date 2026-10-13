using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>A decisão "ninguém mais quer este job" que aborta HLS e remux órfãos — o remux não tinha
/// isso e uma pré-busca nunca assistida remuxava o arquivo inteiro à toa.</summary>
public class InteresseOrfaoTests
{
    private static readonly DateTime Agora = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Noventa = TimeSpan.FromSeconds(90);

    [Fact]
    public void Acesso_recente_nao_e_orfao() =>
        Assert.False(HlsTranscodeService.InteresseExpirou(Agora.AddSeconds(-30), Agora, Noventa));

    [Fact]
    public void Acesso_antigo_e_orfao() =>
        Assert.True(HlsTranscodeService.InteresseExpirou(Agora.AddSeconds(-91), Agora, Noventa));

    [Fact]
    public void Exatamente_no_limite_ainda_nao_e_orfao() =>
        Assert.False(HlsTranscodeService.InteresseExpirou(Agora.AddSeconds(-90), Agora, Noventa));

    [Fact]
    public void Sem_registro_de_acesso_nunca_e_orfao() =>
        Assert.False(HlsTranscodeService.InteresseExpirou(null, Agora, Noventa));

    [Fact]
    public void Timeout_zero_desliga_a_checagem() =>
        Assert.False(HlsTranscodeService.InteresseExpirou(Agora.AddDays(-1), Agora, TimeSpan.Zero));
}
