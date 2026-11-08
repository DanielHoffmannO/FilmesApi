using FilmesApi.Services;

namespace FilmesApi.Tests;

public class RegistroDeMarcacaoTests
{
    [Fact]
    public void Linha_traz_via_posicao_selecionado_e_cliente()
    {
        var linha = RegistroDeMarcacao.Montar(1709, "concluir", null, null, 1744, "192.168.1.50", "Mozilla/5.0 (SMART-TV; Tizen 2.4)");
        Assert.Equal("Episódio 1709 marcado como assistido: via=concluir, posição=?, duração=?, selecionado=1744, cliente=192.168.1.50 Mozilla/5.0 (SMART-TV; Tizen 2.4)", linha);
    }

    [Fact]
    public void Posicao_formatada_sem_cultura_e_sem_selecionado_ou_user_agent()
    {
        var linha = RegistroDeMarcacao.Montar(5, "progresso", 3595.5, 3600, null, null, "");
        Assert.Equal("Episódio 5 marcado como assistido: via=progresso, posição=3595.5s, duração=3600s, selecionado=nenhum, cliente=? -", linha);
    }

    [Fact]
    public void User_agent_longo_e_resumido()
    {
        var linha = RegistroDeMarcacao.Montar(1, "manual", null, null, 1, "10.0.0.2", new string('a', 200));
        Assert.EndsWith(new string('a', 60) + "…", linha);
    }
}
