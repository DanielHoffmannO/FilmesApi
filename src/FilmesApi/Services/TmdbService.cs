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
        return await GarantirTituloLatinoAsync(InterpretarResposta(doc, serie, _imgBase), serie, ct);
    }

    /// <summary>Metadados do item exato <paramref name="id"/> (override manual do nomes.json): <c>/movie/{id}</c> ou
    /// <c>/tv/{id}</c>. <c>null</c> se o id não existe (404) ou a chave foi recusada; lança em erro de transporte,
    /// como <see cref="BuscarAsync"/>.</summary>
    public async Task<TmdbResultado?> BuscarPorIdAsync(int id, bool serie, CancellationToken ct)
    {
        if (!Habilitado || id <= 0) return null;

        var url = $"https://api.themoviedb.org/3/{(serie ? "tv" : "movie")}/{id}?api_key={_apiKey}"
                + $"&language={Uri.EscapeDataString(_lang)}";
        using var cli = _http.CreateClient();
        cli.Timeout = TimeSpan.FromSeconds(15);
        using var resp = await cli.GetAsync(url, ct);

        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _chaveInvalida = true;
            _logger.LogWarning("TMDB recusou a chave ({Code}) — enriquecimento desligado até reiniciar.", (int)resp.StatusCode);
            return null;
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("TMDB: {Tipo}/{Id} não existe (404) — override do nomes.json ignorado.", serie ? "tv" : "movie", id);
            return null;
        }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return await GarantirTituloLatinoAsync(ExtrairItem(doc.RootElement, serie, _imgBase), serie, ct);
    }

    /// <summary>Sem tradução pro português, o TMDB devolve o título original — que pode vir em japonês, coreano, cirílico etc.
    /// Nesse caso busca as traduções do item e fica com a portuguesa (pt-BR, depois pt-PT); sem ela, a inglesa. Se nada
    /// em escrita latina existir, mantém o que veio. Só gasta a chamada extra quando o título localizado não é latino.</summary>
    private async Task<TmdbResultado?> GarantirTituloLatinoAsync(TmdbResultado? res, bool serie, CancellationToken ct)
    {
        if (res is null || !TemEscritaNaoLatina(res.Titulo)) return res;

        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);   // mesma cota do resto do enriquecimento
        var url = $"https://api.themoviedb.org/3/{(serie ? "tv" : "movie")}/{res.TmdbId}/translations?api_key={_apiKey}";
        using var cli = _http.CreateClient();
        cli.Timeout = TimeSpan.FromSeconds(15);
        using var resp = await cli.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return res;   // não vale perder o resto dos metadados por causa do título

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return EscolherTituloLatino(doc, serie) is { } titulo ? res with { Titulo = titulo } : res;
    }

    /// <summary>true se o texto tem alguma letra fora da escrita latina (kana, kanji, hangul, cirílico, árabe...).</summary>
    internal static bool TemEscritaNaoLatina(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (char.IsLetter(c) && c > '\u024F' && !(c >= '\u1E00' && c <= '\u1EFF')) return true;
        return false;
    }

    /// <summary>Do JSON de <c>/translations</c>: título em pt-BR, senão pt-PT, senão inglês — só se estiver em escrita latina.</summary>
    internal static string? EscolherTituloLatino(JsonDocument doc, bool serie)
    {
        if (!doc.RootElement.TryGetProperty("translations", out var lista) || lista.ValueKind != JsonValueKind.Array) return null;
        var campo = serie ? "name" : "title";
        string? Achar(string idioma, string? pais)
        {
            foreach (var t in lista.EnumerateArray())
            {
                if (Str(t, "iso_639_1") != idioma || (pais is not null && Str(t, "iso_3166_1") != pais)) continue;
                if (!t.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) continue;
                var titulo = Vazio(Str(d, campo));
                if (titulo is not null && !TemEscritaNaoLatina(titulo)) return titulo;
            }
            return null;
        }
        return Achar("pt", "BR") ?? Achar("pt", "PT") ?? Achar("pt", null) ?? Achar("en", null);
    }

    /// <summary>Extrai o 1º resultado da busca (título/sinopse/pôster), ou null quando não há
    /// resultado utilizável. Pura — sem rede — pra testar contra JSON de exemplo sem precisar
    /// bater no TMDB de verdade.</summary>
    internal static TmdbResultado? InterpretarResposta(JsonDocument doc, bool serie, string imgBase)
    {
        if (!doc.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;

        return ExtrairItem(results[0], serie, imgBase);
    }

    /// <summary>Um item do TMDB (elemento da busca ou resposta de <c>/movie/{id}</c>/<c>/tv/{id}</c>).</summary>
    internal static TmdbResultado? ExtrairItem(JsonElement r, bool serie, string imgBase)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number
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
            string.IsNullOrEmpty(posterPath) ? null : imgBase + posterPath);
    }

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
