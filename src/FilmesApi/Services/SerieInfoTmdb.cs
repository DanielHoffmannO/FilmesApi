using System.Text.Json;

namespace FilmesApi.Services;

public record ProximoEpisodioTmdb(string? Data, int? Temporada, int? Episodio, string? Titulo);

/// <summary>O que o controle mostra da série (vem do TMDB): ano, nota, gêneros, tamanho, situação, elenco principal e o próximo episódio a sair.</summary>
public record SerieInfoTmdb(
    int TmdbId, string? Titulo, string? Sinopse, int? Ano, double? Nota, IReadOnlyList<string> Generos,
    int? Temporadas, int? Episodios, string? Situacao, ProximoEpisodioTmdb? Proximo, IReadOnlyList<string> Elenco);

/// <summary>Um episódio de uma temporada do TMDB: título, sinopse e duração em minutos (a duração só vale como "aproximada" na lista).</summary>
public record EpisodioTmdb(int Numero, string? Titulo, string? Sinopse, int? Minutos);

/// <summary>Leitura pura do JSON do TMDB (<c>/tv/{id}?append_to_response=credits</c> e <c>/tv/{id}/season/{n}</c>) — testável sem rede.</summary>
public static class SerieInfoTmdbLeitor
{
    public const int ElencoMaximo = 5;

    public static SerieInfoTmdb? Serie(JsonDocument doc)
    {
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return null;

        int? ano = Texto(r, "first_air_date") is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), out var a) ? a : null;
        double? nota = r.TryGetProperty("vote_average", out var n) && n.ValueKind == JsonValueKind.Number && n.GetDouble() > 0 ? Math.Round(n.GetDouble(), 1) : null;
        var generos = Lista(r, "genres", g => Texto(g, "name"));
        var elenco = r.TryGetProperty("credits", out var cr) ? Lista(cr, "cast", c => Texto(c, "name")).Take(ElencoMaximo).ToList() : [];

        ProximoEpisodioTmdb? proximo = null;
        if (r.TryGetProperty("next_episode_to_air", out var p) && p.ValueKind == JsonValueKind.Object)
            proximo = new(Texto(p, "air_date"), Inteiro(p, "season_number"), Inteiro(p, "episode_number"), Texto(p, "name"));

        return new SerieInfoTmdb(id, Texto(r, "name"), Texto(r, "overview"), ano, nota, generos,
            Inteiro(r, "number_of_seasons"), Inteiro(r, "number_of_episodes"), SituacaoEmPortugues(Texto(r, "status")), proximo, elenco);
    }

    public static IReadOnlyList<EpisodioTmdb> Temporada(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array
            ? [.. eps.EnumerateArray().Select(e => Inteiro(e, "episode_number") is int num
                ? new EpisodioTmdb(num, Texto(e, "name"), Texto(e, "overview"), Inteiro(e, "runtime") is > 0 and var m ? m : null) : null).OfType<EpisodioTmdb>()]
            : [];

    /// <summary>O <c>status</c> do TMDB vem sempre em inglês.</summary>
    public static string? SituacaoEmPortugues(string? status) => status switch
    {
        "Returning Series" => "Em exibição",
        "Ended" => "Finalizada",
        "Canceled" => "Cancelada",
        "In Production" => "Em produção",
        "Planned" or "Pilot" => "Planejada",
        null or "" => null,
        _ => status,
    };

    private static string? Texto(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static int? Inteiro(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static List<string> Lista(JsonElement e, string prop, Func<JsonElement, string?> campo) =>
        e.TryGetProperty(prop, out var l) && l.ValueKind == JsonValueKind.Array
            ? [.. l.EnumerateArray().Select(campo).OfType<string>()]
            : [];
}
