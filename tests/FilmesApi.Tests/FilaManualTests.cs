using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>Fila manual "tocar a seguir": ordem, duplicata, limite, saída ao começar a tocar e precedência sobre o próximo episódio.</summary>
public class FilaManualTests
{
    [Fact]
    public void Enfileira_em_ordem_sem_duplicar_e_aparece_no_snapshot()
    {
        var s = new PlayerStateService();
        Assert.Equal(ResultadoFila.Adicionado, s.Enfileirar(10));
        Assert.Equal(ResultadoFila.Adicionado, s.Enfileirar(20));
        Assert.Equal(ResultadoFila.JaNaFila, s.Enfileirar(10));

        Assert.Equal([10, 20], s.Fila);
        dynamic snap = s.Snapshot();
        Assert.Equal(new[] { 10, 20 }, (int[])snap.fila);
    }

    [Fact]
    public void Fila_cheia_recusa_e_remover_e_limpar_funcionam()
    {
        var s = new PlayerStateService();
        for (var i = 1; i <= PlayerStateService.FilaMaxima; i++) Assert.Equal(ResultadoFila.Adicionado, s.Enfileirar(i));
        Assert.Equal(ResultadoFila.Cheia, s.Enfileirar(999));

        Assert.True(s.RemoverDaFila(3));
        Assert.False(s.RemoverDaFila(3));
        Assert.Equal(PlayerStateService.FilaMaxima - 1, s.Fila.Count);

        s.LimparFila();
        Assert.Empty(s.Fila);
    }

    [Fact]
    public void Item_sai_da_fila_quando_comeca_a_tocar_por_qualquer_caminho()
    {
        var s = new PlayerStateService();
        s.Enfileirar(10); s.Enfileirar(20); s.Enfileirar(30);

        s.Selecionar(20);   // escolhido direto no celular ou pela TV

        Assert.Equal([10, 30], s.Fila);
        s.Selecionar(99);   // fora da fila: não mexe nela
        Assert.Equal([10, 30], s.Fila);
    }

    [Fact]
    public async Task Proximo_manual_com_fila_toca_o_primeiro_da_fila_e_nao_ensina_o_automatico()
    {
        using var f = new ScanFixture();
        string[] eps = ["Serie/S01E01.mkv", "Serie/S01E02.mkv", "Outro Filme (2020).mkv"];
        foreach (var e in eps) f.Arquivo(e, new string('x', 2048));
        await f.ScanAsync();
        var cfg = new ConfigurationBuilder().Build();
        var estado = new PlayerStateService();
        var temp = new PreferenciasTemporadaService(f.Escopos);
        var auto = new AutomacaoDoPlayer(f.Escopos, estado, temp, f.Servico<FfmpegOptions>(), cfg, NullLogger<AutomacaoDoPlayer>.Instance);
        var ep1 = await f.IdDeAsync(eps[0]); var ep2 = await f.IdDeAsync(eps[1]); var filme = await f.IdDeAsync(eps[2]);
        estado.Selecionar(ep1);
        estado.ReportarPosicao(1290, 1300);   // no fim: sem fila, o próximo seria o ep2 e ensinaria o automático
        estado.Enfileirar(filme);

        var (proximo, aprendeu) = await auto.ProximoManualAsync();

        Assert.Equal((filme, false), (proximo, aprendeu));
        Assert.Equal(filme, estado.FilmeId);
        Assert.Empty(estado.Fila);

        // fila vazia: volta ao próximo episódio da série
        estado.Selecionar(ep1);
        var (depois, _) = await auto.ProximoManualAsync();
        Assert.Equal(ep2, depois);
    }
}
