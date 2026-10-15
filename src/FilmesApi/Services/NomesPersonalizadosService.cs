using System.Text.Json;

namespace FilmesApi.Services;

/// <summary>
/// Apelidos de série e nomes de exibição num arquivo editável (<c>NomesPath</c>, padrão
/// <c>/data/nomes.json</c>) em vez de dicionário no C# — cada série nova com nome feio custava
/// build + deploy. Formato:
/// <code>{ "apelidos": { "attack on titan": "ataque dos titas" }, "nomes": { "ataque dos titas": "Ataque dos Titãs" } }</code>
/// O padrão embutido (<c>MediaNomeParser.ApelidosPadrao/NomesPadrao</c>) continua valendo; o arquivo soma e
/// sobrescreve. Lido na subida e a cada mudança (olha o arquivo a cada 15 s), ou na hora por
/// <c>POST /api/admin/recarregar-nomes</c>. Arquivo inválido NUNCA derruba o app nem zera o que já
/// valia: loga e segue com os nomes atuais.
/// </summary>
public class NomesPersonalizadosService(IConfiguration config, ILogger<NomesPersonalizadosService> logger) : BackgroundService
{
    public enum Resultado { SemArquivo, Aplicado, Invalido }

    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(15);

    private readonly string _caminho = config.GetValue<string>("NomesPath") ?? "/data/nomes.json";
    private readonly object _trava = new();
    private (DateTime Mtime, long Tamanho)? _visto;   // assinatura do arquivo na última leitura (null = não existia)
    private bool _temPersonalizado;

    public string Caminho => _caminho;

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        Recarregar();
        using var relogio = new PeriodicTimer(Intervalo);
        try
        {
            while (await relogio.WaitForNextTickAsync(parar))
                if (Mudou()) Recarregar();
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Lê o arquivo agora. <c>Apelidos</c>/<c>Nomes</c> = quantas entradas vieram do arquivo.</summary>
    public (Resultado Resultado, int Apelidos, int Nomes) Recarregar()
    {
        lock (_trava)
        {
            var assinatura = Assinatura();
            _visto = assinatura;

            if (assinatura is null)
            {
                if (_temPersonalizado)
                {
                    MediaNomeParser.DefinirNomesPersonalizados(null, null);
                    _temPersonalizado = false;
                    logger.LogInformation("nomes.json sumiu ({Caminho}) — voltando ao padrão embutido.", _caminho);
                }
                return (Resultado.SemArquivo, 0, 0);
            }

            try
            {
                var (apelidos, nomes) = Ler(File.ReadAllText(_caminho));
                MediaNomeParser.DefinirNomesPersonalizados(apelidos, nomes);
                _temPersonalizado = true;
                logger.LogInformation("nomes.json aplicado: {Apelidos} apelido(s), {Nomes} nome(s) de exibição.", apelidos.Count, nomes.Count);
                return (Resultado.Aplicado, apelidos.Count, nomes.Count);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("nomes.json inválido ({Caminho}): {Erro} — os nomes atuais continuam valendo.", _caminho, ex.Message);
                return (Resultado.Invalido, 0, 0);
            }
        }
    }

    private bool Mudou()
    {
        lock (_trava) return Assinatura() != _visto;
    }

    private (DateTime, long)? Assinatura()
    {
        try
        {
            var fi = new FileInfo(_caminho);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Valida e extrai os dois mapas. Lança <see cref="JsonException"/>/<see cref="FormatException"/>
    /// se o arquivo não for o formato esperado; chaves desconhecidas são ignoradas.</summary>
    internal static (Dictionary<string, string> Apelidos, Dictionary<string, string> Nomes) Ler(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("a raiz precisa ser um objeto { \"apelidos\": {...}, \"nomes\": {...} }");

        return (LerMapa(doc.RootElement, "apelidos"), LerMapa(doc.RootElement, "nomes"));
    }

    private static Dictionary<string, string> LerMapa(JsonElement raiz, string nome)
    {
        var mapa = new Dictionary<string, string>();
        if (!raiz.TryGetProperty(nome, out var el)) return mapa;
        if (el.ValueKind != JsonValueKind.Object)
            throw new FormatException($"\"{nome}\" precisa ser um objeto de texto -> texto");
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                throw new FormatException($"\"{nome}\" › \"{p.Name}\" precisa ser texto");
            mapa[p.Name] = p.Value.GetString()!;
        }
        return mapa;
    }
}
