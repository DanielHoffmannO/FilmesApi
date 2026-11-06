using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

public class RegistroDeTarefasTests
{
    [Fact] public void Declarada_aparece_sem_ter_rodado()
    {
        var r = new RegistroDeTarefas();
        r.Declarar("scan"); r.Declarar("metadados", ligada: false);
        Assert.Equal(["aguardando", "desligada"], r.Snapshot().Select(t => t.Estado));
        Assert.All(r.Snapshot(), t => Assert.Null(t.UltimaExecucao));
    }

    [Fact] public void Ok_e_falha_contam_e_guardam_resultado()
    {
        var r = new RegistroDeTarefas();
        r.Iniciar("scan").Ok("2 novos");
        r.Iniciar("scan").Falhou(new IOException("disco caiu"));
        var t = Assert.Single(r.Snapshot());
        Assert.Equal("falhou", t.Estado);
        Assert.Equal((2, 1, "disco caiu"), (t.Execucoes, t.Falhas, t.Erro));
        r.Iniciar("scan").Ok("1 novo");
        t = r.Snapshot()[0];
        Assert.Equal(("ok", "1 novo", null), (t.Estado, t.Resumo, t.Erro));
    }

    [Fact] public void Rodando_ate_concluir_e_medicao_so_conta_uma_vez()
    {
        var r = new RegistroDeTarefas();
        var m = r.Iniciar("limpeza-cache");
        Assert.Equal("rodando", r.Snapshot()[0].Estado);
        m.Ok(); m.Ok(); m.Falhou(new Exception("x"));
        Assert.Equal(1, r.Snapshot()[0].Execucoes);
    }

    [Fact] public async Task Scan_periodico_registra_a_execucao()
    {
        using var f = new ScanFixture();
        var r = new RegistroDeTarefas();
        var servico = new ScanPeriodicoService(f.Escopos, new ConfigurationBuilder().Build(), NullLogger<ScanPeriodicoService>.Instance, r);
        await servico.ExecutarUmaVezAsync();
        var t = Assert.Single(r.Snapshot());
        Assert.Equal(("scan", "ok"), (t.Nome, t.Estado));
        Assert.NotNull(t.DuracaoMs);
    }
}
