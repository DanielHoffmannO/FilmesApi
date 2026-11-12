using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FilmesApi.Services;

/// <summary>O que fazer com um webhook do Sonarr/Radarr.</summary>
public enum AcaoDoWebhook
{
    /// <summary>Corpo que não é um webhook do *arr (vazio, não é JSON, sem <c>eventType</c>).</summary>
    Invalido,
    /// <summary>Botão "Test" do *arr: só responde que está tudo certo.</summary>
    Teste,
    /// <summary>Mexeu em arquivo de vídeo (importou, renomeou, apagou): vale um scan agora.</summary>
    Escanear,
    /// <summary>Evento que não muda nada no disco (Grab, Health, SeriesAdd…): responde 200 e esquece.</summary>
    Ignorar,
}

/// <summary>Resultado de <see cref="WebhookArr.Interpretar"/>. <c>Evento</c> vem normalizado (só nomes conhecidos; o resto
/// vira "desconhecido") pra ir pro log sem repetir texto livre de quem chamou. <c>Origem</c>: "Sonarr", "Radarr" ou "arr".</summary>
public readonly record struct InterpretacaoDoWebhook(AcaoDoWebhook Acao, string Evento, string Origem);

/// <summary>
/// Webhook do Sonarr/Radarr (Settings → Connect → Webhook): quando o *arr importa um arquivo ele avisa o FilmesApi, que
/// roda o scan na hora em vez de esperar o periódico. Lógica pura (chave e payload) — o endpoint está em
/// <c>WebhookArrController</c> e o scan sob demanda em <see cref="ScanPeriodicoService.Solicitar"/>.
/// </summary>
public static class WebhookArr
{
    /// <summary>Chave de configuração: <c>Webhook:Chave</c> (variável de ambiente <c>Webhook__Chave</c>). Vazia = endpoint desligado.</summary>
    public const string ConfigChave = "Webhook:Chave";

    /// <summary>Teto do corpo aceito (o payload de um season pack inteiro tem dezenas de KB; 1 MB sobra).</summary>
    public const int TamanhoMaximoDoCorpo = 1024 * 1024;

    // Eventos do *arr que mudam arquivo de vídeo no disco. Os demais (Grab, Health, SeriesAdd, MovieAdded, ApplicationUpdate,
    // ManualInteractionRequired…) não têm nada pra sincronizar.
    private static readonly string[] EventosQueMexemNoDisco =
        ["Download", "ImportComplete", "Rename", "EpisodeFileDelete", "MovieFileDelete", "SeriesDelete", "MovieDelete"];

    // Todos os eventType conhecidos (Sonarr v3/v4 e Radarr), pra normalizar o nome que vai pro log.
    private static readonly string[] EventosConhecidos =
    [
        "Test", "Grab", "SeriesAdd", "MovieAdded", "HealthRestored", "Health", "ApplicationUpdate", "ManualInteractionRequired",
        .. EventosQueMexemNoDisco,
    ];

    /// <summary>O webhook está ligado? Só com uma chave configurada (não vazia) — sem ela o endpoint responde 404, como se não existisse.</summary>
    public static bool Habilitado(string? chaveConfigurada) => !string.IsNullOrWhiteSpace(chaveConfigurada);

    /// <summary>A chave enviada bate com a configurada? Sem chave configurada NUNCA confere (ao contrário de
    /// <see cref="TokenAdmin.Autorizado"/>, onde vazio libera): aqui vazio é "desligado". Compara os hashes SHA-256 em tempo
    /// constante — nem o tempo nem o tamanho da chave vazam.</summary>
    public static bool ChaveConfere(string? configurada, string? enviada)
    {
        if (!Habilitado(configurada) || string.IsNullOrEmpty(enviada)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(configurada!)), SHA256.HashData(Encoding.UTF8.GetBytes(enviada)));
    }

    /// <summary>Lê o payload padrão de Webhook do Sonarr/Radarr (<c>{"eventType":"Download",...}</c>) e diz o que fazer.
    /// Nunca lança: corpo ruim vira <see cref="AcaoDoWebhook.Invalido"/>.</summary>
    public static InterpretacaoDoWebhook Interpretar(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return Ruim;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Ruim;
            if (!doc.RootElement.TryGetProperty("eventType", out var tipo) || tipo.ValueKind != JsonValueKind.String) return Ruim;

            var evento = EventosConhecidos.FirstOrDefault(e => e.Equals(tipo.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase));
            var origem = doc.RootElement.TryGetProperty("series", out _) ? "Sonarr"
                : doc.RootElement.TryGetProperty("movie", out _) ? "Radarr"
                : "arr";
            if (evento is null) return new(AcaoDoWebhook.Ignorar, "desconhecido", origem);

            var acao = evento == "Test" ? AcaoDoWebhook.Teste
                : EventosQueMexemNoDisco.Contains(evento) ? AcaoDoWebhook.Escanear
                : AcaoDoWebhook.Ignorar;
            return new(acao, evento, origem);
        }
        catch (JsonException) { return Ruim; }
    }

    private static readonly InterpretacaoDoWebhook Ruim = new(AcaoDoWebhook.Invalido, "", "arr");
}
