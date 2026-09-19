using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>
/// RegistrarResultado é o mesmo tipo de remendo load-bearing que já causou bug real em
/// HlsTranscodeService (stall contado como falha de encoder desligava a VPU à toa) — aqui
/// quem decide "desligar rkmpp pro resto do processo" é a contagem de falhas consecutivas.
/// Nunca tinha teste. Os testes evitam tocar DisponivelAsync() quando o resultado dependeria
/// do probe real (ffmpeg/VPU de verdade) — só nos casos em que o curto-circuito
/// (forçado ou já desabilitado) garante que o probe nunca roda.
/// </summary>
public class RkmppCapabilityServiceTests
{
    private static RkmppCapabilityService Novo(bool forcarSoftware = false)
    {
        var dados = forcarSoftware
            ? new Dictionary<string, string?> { ["ForceSoftwareEncoder"] = "true" }
            : new Dictionary<string, string?>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(dados).Build();
        return new RkmppCapabilityService(
            new FfmpegOptions("ffmpeg-nunca-invocado", "ffprobe-nunca-invocado"), config,
            NullLogger<RkmppCapabilityService>.Instance);
    }

    // Snapshot() devolve object (pensado pra página de status, sem contrato forte) — os
    // campos são lidos por reflexão em vez de um tipo próprio só pro teste.
    private static T Campo<T>(object snapshot, string nome) =>
        (T)snapshot.GetType().GetProperty(nome)!.GetValue(snapshot)!;

    [Fact]
    public void Duas_falhas_consecutivas_ainda_nao_desabilita()
    {
        var s = Novo();
        s.RegistrarResultado(false);
        s.RegistrarResultado(false);

        var snap = s.Snapshot();
        Assert.Equal(2, Campo<int>(snap, "falhasConsecutivas"));
        Assert.False(Campo<bool>(snap, "desabilitadoPorFalhas"));
    }

    [Fact]
    public void Tres_falhas_consecutivas_desabilita_pro_resto_do_processo()
    {
        var s = Novo();
        s.RegistrarResultado(false);
        s.RegistrarResultado(false);
        s.RegistrarResultado(false);

        Assert.True(Campo<bool>(s.Snapshot(), "desabilitadoPorFalhas"));
    }

    [Fact]
    public void Sucesso_no_meio_zera_o_contador_e_evita_desabilitar()
    {
        var s = Novo();
        s.RegistrarResultado(false);
        s.RegistrarResultado(false);
        s.RegistrarResultado(true);   // zera -- as 2 falhas anteriores não contam mais
        s.RegistrarResultado(false);
        s.RegistrarResultado(false);

        var snap = s.Snapshot();
        Assert.Equal(2, Campo<int>(snap, "falhasConsecutivas"));
        Assert.False(Campo<bool>(snap, "desabilitadoPorFalhas"));
    }

    [Fact]
    public async Task Desabilitado_por_falhas_bloqueia_sem_nunca_rodar_o_probe()
    {
        var s = Novo();
        s.RegistrarResultado(false);
        s.RegistrarResultado(false);
        s.RegistrarResultado(false);

        Assert.False(await s.DisponivelAsync());
        // probeConcluido continua false -- a resposta veio do curto-circuito de
        // _desabilitadoPorFalhas, o Lazy<Task<bool>> nunca foi avaliado.
        Assert.False(Campo<bool>(s.Snapshot(), "probeConcluido"));
    }

    [Fact]
    public async Task ForceSoftwareEncoder_bloqueia_sem_nunca_rodar_o_probe()
    {
        var s = Novo(forcarSoftware: true);

        Assert.False(await s.DisponivelAsync());
        Assert.False(Campo<bool>(s.Snapshot(), "probeConcluido"));
    }
}
