using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FilmesApi.Services;

/// <summary>Espaço de um volume: o que o <c>/api/status</c> e a <c>status.html</c> mostram.</summary>
public sealed record VolumeEspaco(string Nome, string Caminho, long TotalBytes, long LivreBytes)
{
    public double LivreGB => Math.Round(LivreBytes / 1073741824.0, 1);
    public double PercentualUsado => TotalBytes <= 0 ? 0 : Math.Round(100.0 * (TotalBytes - LivreBytes) / TotalBytes, 1);
}

/// <summary>Lê o espaço livre dos volumes que importam: <c>dados</c> (raiz dos caches <c>CachePath</c>, onde ficam o SQLite, o HLS, o remux
/// e os backups) e <c>midia</c> (<c>MediaPath</c>). Só informa — nunca recusa um job de transcode por falta de espaço.</summary>
public static class EspacoLivre
{
    /// <summary>Volume que contém <paramref name="caminho"/>; <c>null</c> se não der pra ler (pasta ausente, montagem caída).</summary>
    public static VolumeEspaco? Do(string caminho, string nome = "")
    {
        try
        {
            var completo = Path.GetFullPath(caminho);
            // Pasta ainda não criada (ex.: backups/): sobe até o primeiro ancestral que existe; o volume é o mesmo.
            while (!Directory.Exists(completo) && Path.GetDirectoryName(completo) is { } pai) completo = pai;
            if (!Directory.Exists(completo)) return null;
            var d = new DriveInfo(completo);
            return new VolumeEspaco(nome, caminho, d.TotalSize, d.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public static IReadOnlyList<VolumeEspaco> Volumes(IConfiguration config)
    {
        var dados = Do(config.GetValue<string>("CachePath") is { Length: > 0 } c ? c : "/data", "dados");
        var midia = Do(config.GetValue<string>("MediaPath") ?? "/media", "midia");
        return new[] { dados, midia }.OfType<VolumeEspaco>().ToList();
    }

    /// <summary>Mínimo livre (GB) no volume de dados; abaixo disso o <c>/health</c> fica Degraded. <c>0</c> desliga. Padrão 5.</summary>
    public static double MinimoGB(IConfiguration config) => config.GetValue<double?>("EspacoLivreMinimoGB") ?? 5;
}

/// <summary>/health "Degraded" (503 em <c>/health</c>) quando o volume de dados tem menos que <c>EspacoLivreMinimoGB</c> livres — o cache HLS
/// e o SQLite escrevem ali; disco cheio derruba transcode e progresso. Mesmo padrão das montagens: só sinaliza, e
/// <c>/health/vivo</c> (Docker) não roda este check, então nada reinicia por falta de espaço. Não afeta os jobs de transcode.</summary>
public sealed class EspacoHealthCheck(IConfiguration config) : IHealthCheck
{
    public const string Tag = "espaco";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var minimo = EspacoLivre.MinimoGB(config);
        if (minimo <= 0) return Task.FromResult(HealthCheckResult.Healthy("desligado"));
        var dados = EspacoLivre.Volumes(config).FirstOrDefault(v => v.Nome == "dados");
        return Task.FromResult(Avaliar(dados, minimo));
    }

    /// <summary>Sem leitura (null) = não sabemos: saudável, pra não alarmar por uma pasta que o scan/montagens já cobrem.</summary>
    internal static HealthCheckResult Avaliar(VolumeEspaco? dados, double minimoGB) =>
        dados is not null && dados.LivreBytes < minimoGB * 1073741824.0
            ? HealthCheckResult.Degraded($"pouco espaço no volume de dados: {dados.LivreGB:F1} GB livres (mínimo {minimoGB:F1} GB)")
            : HealthCheckResult.Healthy();
}
