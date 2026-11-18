using System.Collections.Concurrent;

namespace FilmesApi.Services;

/// <summary>Quanto demora do "enviar para a TV" até o 1º quadro: marca, no log, cada etapa uma vez por envio ("TV pediu o preparo", "TV começou a baixar")
/// com o tempo desde o clique — pra dar pra medir onde estão os segundos (o remux de áudio é o que costuma pesar).</summary>
public static class LinhaDoTempoDeEnvio
{
    private static readonly ConcurrentDictionary<int, (DateTime Inicio, HashSet<string> Etapas)> Envios = new();

    public static void Iniciar(ILogger log, int filmeId, bool remuxEmCache)
    {
        Envios[filmeId] = (DateTime.UtcNow, []);
        if (Envios.Count > 50) foreach (var k in Envios.Where(e => DateTime.UtcNow - e.Value.Inicio > TimeSpan.FromHours(1)).Select(e => e.Key).ToList()) Envios.TryRemove(k, out _);
        log.LogInformation("Envio para a TV: filme {Id} escolhido (cópia mp4 em cache: {Cache}).", filmeId, remuxEmCache ? "sim" : "não");
    }

    /// <summary>Registra a etapa (uma vez por envio) e o tempo desde o clique. Sem envio recente deste filme, não faz nada.</summary>
    public static void Marcar(ILogger log, int filmeId, string etapa)
    {
        if (!Envios.TryGetValue(filmeId, out var e) || DateTime.UtcNow - e.Inicio > TimeSpan.FromMinutes(10)) return;
        lock (e.Etapas) if (!e.Etapas.Add(etapa)) return;
        log.LogInformation("Envio para a TV: filme {Id} — {Etapa} após {Seg:F1} s do clique.", filmeId, etapa, (DateTime.UtcNow - e.Inicio).TotalSeconds);
    }
}
