using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FilmesApi.Services;

/// <summary>Uma pasta de 1º nível da mídia (cada uma pode ser um disco/montagem): quantos itens o catálogo tem nela,
/// quantos arquivos de vídeo existem de verdade, e se o scan acha que ela caiu. <c>Estado</c>: <c>ok</c> ou
/// <c>suspeita</c> (itens no catálogo, mas o scan não os removeu porque a pasta/disco parece ter caído — ver
/// <see cref="FilmeService.PrefixosRemoviveis"/>).</summary>
public record MontagemDiag(string Prefixo, int ItensNoCatalogo, int ArquivosNoDisco, string Estado);

/// <summary>O resultado do ÚLTIMO scan, guardado pra <c>/api/status</c> e <c>/health</c> responderem sem varrer o disco
/// de novo. Antes do primeiro scan não sabe nada (lista vazia, saudável).</summary>
public class EstadoMontagens
{
    public const string Ok = "ok";
    public const string Suspeita = "suspeita";

    private sealed record Instantaneo(IReadOnlyList<MontagemDiag> Montagens, DateTime Quando);

    private volatile Instantaneo? _atual;

    public IReadOnlyList<MontagemDiag> Montagens => _atual?.Montagens ?? [];
    public DateTime? AtualizadoEm => _atual?.Quando;
    public IReadOnlyList<MontagemDiag> Suspeitas => Montagens.Where(m => m.Estado == Suspeita).ToList();

    public void Atualizar(IReadOnlyList<MontagemDiag> montagens) => _atual = new Instantaneo(montagens, DateTime.UtcNow);

    /// <summary>Monta a lista a partir do que o scan já sabe. Suspeita = tem item no catálogo, NENHUM arquivo no disco e o
    /// prefixo não está entre os removíveis — ou seja, os órfãos dele foram mantidos por precaução. Prefixo com arquivo
    /// no disco nunca é suspeito (inclusive o recém-importado, que ainda não estava na lista "do banco" do scan).
    /// Pasta apagada de propósito já teve os itens removidos no próprio scan: sem item, não aparece (e está saudável).</summary>
    internal static List<MontagemDiag> Classificar(
        IReadOnlyDictionary<string, int> itensPorPrefixo, IReadOnlyDictionary<string, int> arquivosPorPrefixo,
        IReadOnlySet<string> removiveis) =>
        itensPorPrefixo.Keys.Concat(arquivosPorPrefixo.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var itens = itensPorPrefixo.GetValueOrDefault(p);
                var arquivos = arquivosPorPrefixo.GetValueOrDefault(p);
                return new MontagemDiag(
                    p.Length == 0 ? "(raiz)" : p, itens, arquivos,
                    itens > 0 && arquivos == 0 && !removiveis.Contains(p) ? Suspeita : Ok);
            })
            .ToList();
}

/// <summary>/health "Degraded" (503 em <c>/health</c>) quando alguma pasta com itens no catálogo está em suspeita — pra o
/// Uptime Kuma avisar disco caído em vez de o dono descobrir num 404. Só sinaliza: <c>/health/vivo</c> (usado pelo
/// healthcheck do Docker) segue 200 nesse caso, então nada reinicia por isso.</summary>
public class MontagensHealthCheck(EstadoMontagens estado) : IHealthCheck
{
    public const string Tag = "montagens";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var suspeitas = estado.Suspeitas;
        return Task.FromResult(suspeitas.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("montagem caída? sem arquivos no disco, itens mantidos: " + string.Join(", ", suspeitas.Select(s => s.Prefixo))));
    }
}
