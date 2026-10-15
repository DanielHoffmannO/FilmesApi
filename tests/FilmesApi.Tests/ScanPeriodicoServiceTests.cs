using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

public class ScanPeriodicoServiceTests
{
    [Fact]
    public async Task Uma_execucao_importa_o_que_chegou_no_disco()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Hora.de.Aventura.S05E05.mkv");
        var servico = new ScanPeriodicoService(f.Escopos, new ConfigurationBuilder().Build(), NullLogger<ScanPeriodicoService>.Instance);

        await servico.ExecutarUmaVezAsync();

        Assert.Single(await f.CaminhosNoBancoAsync());
    }

    [Fact]
    public async Task Falha_no_scan_nao_derruba_o_servico()
    {
        // pasta de mídia apagada no meio do caminho + banco válido: nada a lançar pra fora
        using var f = new ScanFixture();
        var servico = new ScanPeriodicoService(f.Escopos, new ConfigurationBuilder().Build(), NullLogger<ScanPeriodicoService>.Instance);
        Directory.Delete(f.Media, recursive: true);

        await servico.ExecutarUmaVezAsync();   // não pode lançar
    }

    [Fact]
    public async Task Intervalo_zero_desliga_e_o_servico_termina_na_hora()
    {
        using var f = new ScanFixture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ScanIntervaloMinutos"] = "0" }).Build();
        var servico = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);

        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));   // terminou sozinho, sem timer
        await servico.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Parar_o_servico_com_timer_ligado_termina_limpo()
    {
        using var f = new ScanFixture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ScanIntervaloMinutos"] = "60" }).Build();
        var servico = new ScanPeriodicoService(f.Escopos, config, NullLogger<ScanPeriodicoService>.Instance);

        await servico.StartAsync(CancellationToken.None);
        await servico.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
