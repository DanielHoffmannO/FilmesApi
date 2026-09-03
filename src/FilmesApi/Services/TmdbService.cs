using System.Text.Json;

namespace FilmesApi.Services;

/// <summary><c>Titulo</c> = título localizado e limpo do TMDB, pra exibir no lugar do nome
/// de arquivo scene ("...WOLVERDONFILMES COM").</summary>
public record TmdbResultado(int TmdbId, string? Titulo, string? TituloOriginal, string? Sinopse, string? PosterUrl);

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

    private volatile bool _chaveInvalida;
    public bool Habilitado => !string.IsNullOrWhiteSpace(_apiKey) && !_chaveInvalida;

    /// <summary>Primeiro resultado da busca, ou <c>null</c> quando o TMDB não achou nada.
    /// <b>Lança</b> em erro de transporte (rede/timeout/5xx/JSON malformado) — o chamador
    /// deve deixar o filme pendente, não marcar como processado.</summary>
    public async Task<TmdbResultado?> BuscarAsync(string titulo, int? ano, bool serie, CancellationToken ct)
    {
        if (!Habilitado || string.IsNullOrWhiteSpace(titulo)) return null;

        var tipo = serie ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/search/{tipo}?api_key={_apiKey}"
                + $"&language={Uri.EscapeDataString(_lang)}&include_adult=false"
                + $"&query={Uri.EscapeDataString(titulo)}";
        if (ano is int a) url += serie ? $"&first_air_date_year={a}" : $"&year={a}";

        using var cli = _http.CreateClient();
        cli.Timeout = TimeSpan.FromSeconds(15);
        using var resp = await cli.GetAsync(url, ct);

        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _chaveInvalida = true;
            _logger.LogWarning("TMDB recusou a chave ({Code}) — enriquecimento desligado até reiniciar.", (int)resp.StatusCode);
            return null;
        }
        resp.EnsureSuccessStatusCode();  // 5xx/429 -> exceção -> filme fica pendente

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;

        var r = results[0];
        if (!r.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number
            || !idEl.TryGetInt32(out var id) || id == 0)
            return null;

        var tituloLimpo = serie ? Str(r, "name") : Str(r, "title");
        var original = serie ? Str(r, "original_name") : Str(r, "original_title");
        var sinopse = Str(r, "overview");
        var posterPath = Str(r, "poster_path");

        return new TmdbResultado(
            id,
            Vazio(tituloLimpo),
            Vazio(original),
            Vazio(sinopse),
            string.IsNullOrEmpty(posterPath) ? null : _imgBase + posterPath);
    }

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
