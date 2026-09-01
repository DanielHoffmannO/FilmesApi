using System.Text.Json;

namespace FilmesApi.Services;

public record TmdbResultado(int TmdbId, string? TituloOriginal, string? Sinopse, string? PosterUrl);

/// <summary>
/// Busca metadados (título oficial, sinopse, pôster) no TMDB a partir do nome do arquivo.
/// Só liga se <c>TmdbApiKey</c> estiver configurada — sem chave, <see cref="Habilitado"/> é
/// false e o catálogo segue com a classificação por nome de arquivo, sem pôster.
/// </summary>
public class TmdbService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TmdbService> _logger;
    private readonly string? _apiKey;
    private readonly string _lang;
    private readonly string _imgBase;

    public TmdbService(IHttpClientFactory http, IConfiguration config, ILogger<TmdbService> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = config.GetValue<string>("TmdbApiKey");
        _lang = config.GetValue<string>("TmdbLanguage") ?? "pt-BR";
        _imgBase = (config.GetValue<string>("TmdbImageBase") ?? "https://image.tmdb.org/t/p/w342").TrimEnd('/');
    }

    public bool Habilitado => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>Primeiro resultado da busca no TMDB, ou null (sem chave, sem match, ou erro de rede).</summary>
    public async Task<TmdbResultado?> BuscarAsync(string titulo, int? ano, bool serie, CancellationToken ct)
    {
        if (!Habilitado || string.IsNullOrWhiteSpace(titulo)) return null;

        var tipo = serie ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/search/{tipo}?api_key={_apiKey}"
                + $"&language={Uri.EscapeDataString(_lang)}&include_adult=false"
                + $"&query={Uri.EscapeDataString(titulo)}";
        if (ano is int a) url += serie ? $"&first_air_date_year={a}" : $"&year={a}";

        try
        {
            using var cli = _http.CreateClient();
            cli.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await cli.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB respondeu {Code} pra \"{Titulo}\".", (int)resp.StatusCode, titulo);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;

            var r = results[0];
            var id = r.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            if (id == 0) return null;

            string? original = serie
                ? Str(r, "original_name") : Str(r, "original_title");
            string? sinopse = Str(r, "overview");
            string? posterPath = Str(r, "poster_path");
            string? poster = string.IsNullOrEmpty(posterPath) ? null : _imgBase + posterPath;

            return new TmdbResultado(id, original, string.IsNullOrWhiteSpace(sinopse) ? null : sinopse, poster);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Falha ao consultar o TMDB pra \"{Titulo}\".", titulo);
            return null;
        }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
