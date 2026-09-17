using FilmesApi.Services;
using static FilmesApi.Services.ProgressoService;

namespace FilmesApi.Tests;

/// <summary>
/// DecidirSalvamento é a regra de negócio de "continuar assistindo": quando descarta (cedo
/// demais / perto demais do fim), quando marca assistido, quando insere/atualiza. Extraída
/// pura de <see cref="ProgressoService.SalvarAsync"/> pra testar sem precisar de banco.
/// </summary>
public class ProgressoServiceTests
{
    [Fact]
    public void Posicao_abaixo_do_minimo_descarta()
    {
        // MinSegundosParaSalvar = 15 -- 10s é "começou agora", não vale guardar retomada.
        var d = DecidirSalvamento(posicao: 10, duracao: 3600, jaTemProgresso: false);
        Assert.Equal(DecisaoSalvarProgresso.Descartar, d);
    }

    [Fact]
    public void Posicao_no_minimo_exato_ja_guarda()
    {
        var d = DecidirSalvamento(posicao: 15, duracao: 3600, jaTemProgresso: false);
        Assert.Equal(DecisaoSalvarProgresso.Inserir, d);
    }

    [Fact]
    public void Perto_do_fim_de_filme_longo_marca_assistido_em_vez_de_guardar_retomada()
    {
        // Filme de 1h (3600s): margem de fim = min(90, 360) = 90s. Parar em 3550s (50s do fim)
        // cai dentro da margem.
        var d = DecidirSalvamento(posicao: 3550, duracao: 3600, jaTemProgresso: true);
        Assert.Equal(DecisaoSalvarProgresso.DescartarEMarcarAssistido, d);
    }

    [Fact]
    public void Video_curto_usa_margem_de_10_porcento_em_vez_dos_90s_fixos()
    {
        // Trailer de 60s: 90s fixos cobririam o vídeo inteiro (nunca guardaria retomada
        // nenhuma). Margem real = min(90, 6) = 6s -- só os últimos 6s contam como "no fim".
        var noMeio = DecidirSalvamento(posicao: 30, duracao: 60, jaTemProgresso: false);
        var pertoDoFim = DecidirSalvamento(posicao: 55, duracao: 60, jaTemProgresso: false);

        Assert.Equal(DecisaoSalvarProgresso.Inserir, noMeio);
        Assert.Equal(DecisaoSalvarProgresso.DescartarEMarcarAssistido, pertoDoFim);
    }

    [Fact]
    public void Duracao_desconhecida_hls_convertendo_nunca_marca_assistido_so_pela_posicao()
    {
        // Sem duracao (HLS ainda convertendo, ver duracaoConfiavel no front) não dá pra saber
        // se está perto do fim -- só guarda/atualiza normalmente, nunca "pertoDoFim".
        var d = DecidirSalvamento(posicao: 999999, duracao: null, jaTemProgresso: false);
        Assert.Equal(DecisaoSalvarProgresso.Inserir, d);
    }

    [Fact]
    public void Ja_tem_progresso_e_posicao_valida_atualiza_em_vez_de_inserir()
    {
        var d = DecidirSalvamento(posicao: 100, duracao: 3600, jaTemProgresso: true);
        Assert.Equal(DecisaoSalvarProgresso.Atualizar, d);
    }

    [Fact]
    public void Nao_tem_progresso_e_posicao_valida_insere()
    {
        var d = DecidirSalvamento(posicao: 100, duracao: 3600, jaTemProgresso: false);
        Assert.Equal(DecisaoSalvarProgresso.Inserir, d);
    }
}
