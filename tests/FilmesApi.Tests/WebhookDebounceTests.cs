using FilmesApi.Services;
using Microsoft.Extensions.Configuration;

namespace FilmesApi.Tests;

/// <summary><see cref="DebounceDeScan"/>: uma rajada de webhooks (um por episódio importado) vira UM scan; o teto impede que
/// uma rajada comprida adie o scan pra sempre. O "agora" é um instante monotônico passado por parâmetro.</summary>
public class WebhookDebounceTests
{
    private static TimeSpan S(double segundos) => TimeSpan.FromSeconds(segundos);

    [Fact]
    public void Sem_pedido_nao_ha_o_que_colher()
    {
        var d = new DebounceDeScan(S(12), S(60));

        Assert.False(d.Pendente);
        Assert.Null(d.TempoAteVencer(S(100)));
        Assert.False(d.TentarColher(S(100)));
    }

    [Fact]
    public void Um_pedido_vence_depois_da_espera()
    {
        var d = new DebounceDeScan(S(12), S(60));
        d.Solicitar(S(100));

        Assert.True(d.Pendente);
        Assert.Equal(S(12), d.TempoAteVencer(S(100)));
        Assert.Equal(S(2), d.TempoAteVencer(S(110)));
        Assert.False(d.TentarColher(S(111.9)));
        Assert.True(d.TentarColher(S(112)));
        Assert.False(d.Pendente);
    }

    [Fact]
    public void Rajada_de_pedidos_vira_um_scan_so()
    {
        var d = new DebounceDeScan(S(12), S(60));
        // 10 episódios importados, um a cada 3 s
        for (var i = 0; i < 10; i++) d.Solicitar(S(100 + 3 * i));

        Assert.False(d.TentarColher(S(127)));          // o último pedido foi em 127: só vence em 139
        Assert.Equal(S(12), d.TempoAteVencer(S(127)));
        Assert.True(d.TentarColher(S(139)));
        Assert.False(d.TentarColher(S(500)));          // já colhido: nada de segundo scan
    }

    [Fact]
    public void Cada_pedido_novo_adia_o_vencimento()
    {
        var d = new DebounceDeScan(S(12), S(60));
        d.Solicitar(S(0));
        d.Solicitar(S(10));

        Assert.False(d.TentarColher(S(12)));           // sem o 2º pedido venceria aqui
        Assert.True(d.TentarColher(S(22)));
    }

    [Fact]
    public void Rajada_comprida_nao_adia_alem_do_teto()
    {
        var d = new DebounceDeScan(S(12), S(30));
        for (var t = 0; t <= 100; t += 5) d.Solicitar(S(t));   // pedidos de 5 em 5 s, sem nunca ficar 12 s calado

        Assert.True(d.TentarColher(S(100)));           // teto de 30 s desde o 1º pedido (em 0) já passou
    }

    [Fact]
    public void Teto_conta_do_primeiro_pedido_da_rajada()
    {
        var d = new DebounceDeScan(S(12), S(30));
        d.Solicitar(S(0));
        d.Solicitar(S(10));
        d.Solicitar(S(20));
        d.Solicitar(S(29));

        Assert.Equal(S(1), d.TempoAteVencer(S(29)));   // pela espera seria 41; o teto manda: 30
        Assert.True(d.TentarColher(S(30)));
    }

    [Fact]
    public void Pedido_depois_de_colher_comeca_outra_rajada()
    {
        var d = new DebounceDeScan(S(12), S(30));
        d.Solicitar(S(0));
        Assert.True(d.TentarColher(S(12)));

        d.Solicitar(S(50));

        Assert.True(d.Pendente);
        Assert.Equal(S(12), d.TempoAteVencer(S(50)));  // o teto recomeça: não herda o 0 da rajada anterior
        Assert.True(d.TentarColher(S(62)));
    }

    [Fact]
    public void Espera_zero_vence_na_hora()
    {
        var d = new DebounceDeScan(TimeSpan.Zero, TimeSpan.Zero);
        d.Solicitar(S(5));

        Assert.Equal(TimeSpan.Zero, d.TempoAteVencer(S(5)));
        Assert.True(d.TentarColher(S(5)));
    }

    [Fact]
    public void Teto_menor_que_a_espera_vira_a_propria_espera()
    {
        var d = new DebounceDeScan(S(12), S(5));
        d.Solicitar(S(0));

        Assert.False(d.TentarColher(S(11)));
        Assert.True(d.TentarColher(S(12)));
    }

    [Fact]
    public void Tempo_que_ja_passou_do_vencimento_e_zero_nao_negativo()
    {
        var d = new DebounceDeScan(S(12), S(60));
        d.Solicitar(S(0));

        Assert.Equal(TimeSpan.Zero, d.TempoAteVencer(S(500)));
    }

    [Fact]
    public void Pedidos_de_varias_threads_viram_um_scan_so()
    {
        var d = new DebounceDeScan(S(12), S(60));
        Parallel.For(0, 200, i => d.Solicitar(S(1 + i * 0.01)));

        Assert.True(d.TentarColher(S(100)));
        Assert.False(d.TentarColher(S(100)));
    }

    [Fact]
    public void Padroes_sao_12s_e_60s_e_a_configuracao_sobrescreve()
    {
        var padrao = DebounceDeScan.De(new ConfigurationBuilder().Build());
        padrao.Solicitar(S(0));
        Assert.Equal(S(12), padrao.TempoAteVencer(S(0)));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Webhook:DebounceSegundos"] = "3",
            ["Webhook:EsperaMaximaSegundos"] = "4",
        }).Build();
        var custom = DebounceDeScan.De(config);
        custom.Solicitar(S(0));
        custom.Solicitar(S(3));
        Assert.Equal(S(1), custom.TempoAteVencer(S(3)));   // teto 4 manda (pela espera seria 6)
    }
}
