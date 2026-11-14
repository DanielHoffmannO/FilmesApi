using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>A fileira "Dublados": o que tem áudio em português (dublagem de verdade ou por IA — <see cref="DublagemEmPortugues.Qualquer"/>), no mesmo
/// formato da fileira de novidades (<see cref="ItemNovidade"/>: <c>{filme, novos, rotulo}</c>). Um cartão por série, apontando pro próximo episódio
/// dublado que ainda não foi visto (o primeiro, na ordem temporada/episódio; com tudo visto, o primeiro episódio dublado), com rótulo
/// <c>"Série · 12 episódios dublados"</c> (<c>Novos</c> = quantos desses ainda não foram vistos); filme solto é um cartão simples. Quem tem algo
/// por ver vem antes; dentro de cada grupo, os adicionados há menos tempo primeiro. Lógica pura — não toca banco, por isso testável direto.</summary>
public static class Dublados
{
    public const int LimitePadrao = 24;
    public const int LimiteMaximo = 100;

    /// <param name="todos">O catálogo inteiro, já classificado e marcado (<see cref="FilmeService.ListarAsync"/>).</param>
    /// <param name="limite">Quantos cartões, no máximo (depois de colapsar).</param>
    public static List<ItemNovidade> Montar(List<FilmeResponse> todos, int limite = LimitePadrao)
    {
        if (limite <= 0) return [];
        var dublados = todos.Where(f => !f.EhExtra && (f.Dublado || f.DubladoIa)).ToList();   // extra (trailer/sample) nunca é cartão

        var cartoes = new List<(bool PorVer, DateTime Quando, int Id, ItemNovidade Item)>();
        foreach (var f in dublados.Where(f => !f.EhEpisodio))
            cartoes.Add((!f.Assistido, f.DataAdicionado, f.Id, new ItemNovidade(f, f.Assistido ? 0 : 1, f.Titulo)));

        // Mesmo agrupamento da tela: SerieChave (ou o nome cru, se não houver chave).
        foreach (var serie in dublados.Where(f => f.EhEpisodio).GroupBy(f => f.SerieChave ?? f.Serie ?? ""))
        {
            var episodios = serie.ToList();
            CatalogoAgrupador.OrdenarEpisodios(episodios);
            var porVer = episodios.Count(f => !f.Assistido);
            var alvo = episodios.FirstOrDefault(f => !f.Assistido) ?? episodios[0];
            var nome = MediaNomeParser.NomeExibicao(serie.Key.Length > 0 ? serie.Key : null, NomeMaisFrequente(episodios));
            var rotuloEp = RotuloDoEpisodio(alvo);
            var rotulo = episodios.Count > 1 ? $"{nome} · {episodios.Count} episódios dublados"
                : rotuloEp.Length > 0 ? $"{nome} · {rotuloEp}"
                : nome;
            cartoes.Add((porVer > 0, episodios.Max(f => f.DataAdicionado), alvo.Id, new ItemNovidade(alvo, porVer, rotulo)));
        }

        return cartoes.OrderByDescending(c => c.PorVer).ThenByDescending(c => c.Quando).ThenByDescending(c => c.Id)
            .Take(limite).Select(c => c.Item).ToList();
    }

    /// <summary>"S02E05" + o título do episódio, quando o rótulo ainda não o mostra (mesma regra do <c>rotuloComTitulo</c> das telas).</summary>
    private static string RotuloDoEpisodio(FilmeResponse f)
    {
        var titulo = f.TituloEpisodio;
        if (string.IsNullOrEmpty(titulo) || f.Rotulo.Contains(titulo, StringComparison.Ordinal)) return f.Rotulo;
        return f.Rotulo.Length > 0 ? $"{f.Rotulo} · {titulo}" : titulo;
    }

    // O nome de exibição que a tela usa é o mais frequente entre os episódios da série (ver CatalogoAgrupador).
    private static string NomeMaisFrequente(List<FilmeResponse> episodios) => episodios
        .GroupBy(f => f.Serie ?? "")
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .First().Key;
}
