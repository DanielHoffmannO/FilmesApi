using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Um item da fila do controle: <c>Motivo</c> é "continuar" (pela metade) ou "proximo" (o episódio seguinte
/// de uma série vista há pouco).</summary>
public record ItemFila(FilmeResponse Filme, string Motivo);

/// <summary>"O que assistir agora" no controle: primeiro o que ficou pela metade, depois o próximo episódio de cada série
/// vista recentemente. Lógica pura — não toca banco, por isso testável direto.</summary>
public static class FilaContinuar
{
    public const int JanelaDias = 14;

    /// <param name="todos">O catálogo inteiro.</param>
    /// <param name="pelaMetade">Já na ordem (mais recente primeiro) e com posição preenchida.</param>
    /// <param name="assistidoEm">Quando cada episódio virou assistido (só quem tem data entra).</param>
    /// <param name="agora">Referência da janela de <see cref="JanelaDias"/>.</param>
    /// <param name="limite">Tamanho máximo da fila.</param>
    public static List<ItemFila> Montar(
        List<FilmeResponse> todos, List<FilmeResponse> pelaMetade,
        IReadOnlyDictionary<int, DateTime> assistidoEm, DateTime agora, int limite = 12)
    {
        var seriesComRetomada = pelaMetade.Where(f => f.EhEpisodio).Select(f => f.SerieChave).ToHashSet();
        var corte = agora.AddDays(-JanelaDias);

        var proximos = todos
            .Where(f => f.EhEpisodio && !f.EhExtra && f.SerieChave is not null && !seriesComRetomada.Contains(f.SerieChave))
            .GroupBy(f => f.SerieChave!)
            .Where(g => g.Any(f => assistidoEm.ContainsKey(f.Id)))   // só ordena as séries mexidas na janela
            .Select(g => ProximaDaSerie(g.ToList(), assistidoEm, corte))
            .OfType<(FilmeResponse Proximo, DateTime Visto)>()
            .OrderByDescending(x => x.Visto)
            .Select(x => new ItemFila(x.Proximo, "proximo"));

        return pelaMetade.Select(f => new ItemFila(f, "continuar")).Concat(proximos).Take(limite).ToList();
    }

    /// <summary>O episódio não assistido depois do último que o dono terminou (o primeiro; ou o mais novo, se ele costuma pular) — null se ele não terminou nada
    /// na janela, se a série acabou ou se o próximo não existe no acervo.</summary>
    private static (FilmeResponse Proximo, DateTime Visto)? ProximaDaSerie(
        List<FilmeResponse> episodios, IReadOnlyDictionary<int, DateTime> assistidoEm, DateTime corte)
    {
        CatalogoAgrupador.OrdenarEpisodios(episodios);

        var ultimo = episodios.FindLastIndex(f => f.Assistido && assistidoEm.TryGetValue(f.Id, out var em) && em >= corte);
        if (ultimo < 0) return null;
        var adiante = episodios.Skip(ultimo + 1).Where(f => !f.Assistido).ToList();
        // quem já pulou episódios (buraco antes do último que viu) vai direto ao mais novo; quem vê em sequência, ao seguinte
        var temBuraco = episodios.Take(ultimo).Any(f => !f.Assistido);
        var proximo = temBuraco ? adiante.LastOrDefault() : adiante.FirstOrDefault();
        return proximo is null ? null : (proximo, assistidoEm[episodios[ultimo].Id]);
    }
}
