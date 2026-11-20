using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Um episódio na lista do controle. <c>Minutos</c>/<c>DuracaoSegundos</c>: a duração real (de quando já tocou aqui) ou, senão, a aproximada do TMDB.</summary>
public record EpisodioDaLista(int Id, int Episodio, string Rotulo, string? Titulo, double? DuracaoSegundos, bool DuracaoAproximada,
    bool Assistido, double? PosicaoSegundos, string? Sinopse);

public record TemporadaDaLista(int Numero, int Episodios);

public record ListaDeEpisodiosResponse(string? Serie, int Temporada, IReadOnlyList<TemporadaDaLista> Temporadas, IReadOnlyList<EpisodioDaLista> Episodios);

/// <summary>Junta os episódios do catálogo com o que o TMDB sabe da temporada (título, sinopse, duração). Pura — sem banco nem rede.</summary>
public static class ListaDeEpisodios
{
    public static ListaDeEpisodiosResponse Montar(IReadOnlyList<FilmeResponse> episodios, int temporada, IReadOnlyList<EpisodioTmdb>? tmdb)
    {
        var temporadas = episodios.Where(e => e.Temporada is not null).GroupBy(e => e.Temporada!.Value).OrderBy(g => g.Key)
            .Select(g => new TemporadaDaLista(g.Key, g.Count())).ToList();
        var porNumero = (tmdb ?? []).GroupBy(t => t.Numero).ToDictionary(g => g.Key, g => g.First());

        var itens = episodios.Where(e => e.Temporada == temporada && e.Episodio is not null).Select(e =>
        {
            var t = porNumero.GetValueOrDefault(e.Episodio!.Value);
            var real = e.DuracaoSegundos is > 0 ? e.DuracaoSegundos : null;
            return new EpisodioDaLista(
                e.Id, e.Episodio!.Value, e.Rotulo,
                e.TituloEpisodio ?? t?.Titulo,
                real ?? (t?.Minutos is int m ? m * 60.0 : null), real is null && t?.Minutos is not null,
                e.Assistido, e.PosicaoSegundos is > 0 ? e.PosicaoSegundos : null, t?.Sinopse);
        }).ToList();

        return new ListaDeEpisodiosResponse(episodios.FirstOrDefault()?.Serie, temporada, temporadas, itens);
    }
}
