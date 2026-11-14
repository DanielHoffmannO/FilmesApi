using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FilmesApi.Tests;

/// <summary>Espaço livre: o /health fica Degraded abaixo do mínimo configurado (e o /health/vivo, que não roda o check, não).</summary>
public class EspacoLivreTests
{
    private const long GB = 1073741824;

    [Theory]
    [InlineData(2, 5, HealthStatus.Degraded)]
    [InlineData(5, 5, HealthStatus.Healthy)]     // exatamente no limite ainda está bem
    [InlineData(50, 5, HealthStatus.Healthy)]
    public void Avalia_contra_o_minimo(long livreGB, double minimoGB, HealthStatus esperado) =>
        Assert.Equal(esperado, EspacoHealthCheck.Avaliar(new VolumeEspaco("dados", "/data", 100 * GB, livreGB * GB), minimoGB).Status);

    [Fact]
    public void Volume_ilegivel_nao_alarma() =>
        Assert.Equal(HealthStatus.Healthy, EspacoHealthCheck.Avaliar(null, 5).Status);

    [Fact]
    public void Percentual_e_livre_em_gb()
    {
        var v = new VolumeEspaco("dados", "/data", 200 * GB, 50 * GB);
        Assert.Equal(75.0, v.PercentualUsado);
        Assert.Equal(50.0, v.LivreGB);
        Assert.Equal(0, new VolumeEspaco("x", "/", 0, 0).PercentualUsado);
    }

    [Fact]
    public async Task Check_real_le_o_disco_e_zero_desliga()
    {
        var dir = Path.GetTempPath();
        IConfiguration Cfg(string min) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CachePath"] = dir, ["MediaPath"] = dir, ["EspacoLivreMinimoGB"] = min }).Build();

        // Mínimo absurdo: qualquer disco real fica abaixo. Zero desliga e fica saudável.
        Assert.Equal(HealthStatus.Degraded, (await new EspacoHealthCheck(Cfg("999999999")).CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(HealthStatus.Healthy, (await new EspacoHealthCheck(Cfg("0")).CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(["dados", "midia"], EspacoLivre.Volumes(Cfg("5")).Select(v => v.Nome));
    }

    [Fact]
    public void Pasta_ainda_nao_criada_usa_o_volume_do_ancestral() =>
        Assert.NotNull(EspacoLivre.Do(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "backups")));
}
