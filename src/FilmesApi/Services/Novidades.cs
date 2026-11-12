using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Um cartão da fileira "Novidades". <c>Filme</c> é o item que o cartão toca (mesmo formato do catálogo; numa série
/// colapsada, o episódio mais novo ainda não assistido). <c>Novos</c> = quantos itens novos o cartão representa (1 num filme ou
/// episódio solto). <c>Rotulo</c> já vem pronto pra desenhar ("Filme", "Série · S02E05" ou "Série · +3 episódios novos").</summary>
public record ItemNovidade(FilmeResponse Filme, int Novos, string Rotulo);

/// <summary>"O que chegou" no topo da home: itens adicionados há pouco (<c>DataAdicionado</c>) e ainda não assistidos, com os
/// episódios da mesma série colapsados num cartão só. Lógica pura — não toca banco, por isso testável direto.</summary>
public static class Novidades
{
    public const int JanelaDiasPadrao = 14;
    public const int LimitePadrao = 20;
    public const int LimiteMaximo = 100;

    /// <param name="todos">O catálogo inteiro, já classificado (<see cref="FilmeService.ListarAsync"/>).</param>
    /// <param name="agora">Referência da janela.</param>
    /// <param name="limite">Quantos cartões, no máximo (depois de colapsar).</param>
    /// <param name="janelaDias">Só entra o que foi adicionado nos últimos N dias; 0 ou menos = sem corte por data.</param>
    public static List<ItemNovidade> Montar(List<FilmeResponse> todos, DateTime agora, int limite = LimitePadrao, int janelaDias = JanelaDiasPadrao)
    {
        if (limite <= 0) return [];
        var corte = janelaDias > 0 ? agora.AddDays(-janelaDias) : DateTime.MinValue;

        // Extra (trailer/sample) nunca é novidade; assistido também não.
        var novos = todos.Where(f => !f.Assistido && !f.EhExtra && f.DataAdicionado >= corte).ToList();

        var cartoes = new List<(DateTime Quando, int Id, ItemNovidade Item)>();
        foreach (var f in novos.Where(f => !f.EhEpisodio))
            cartoes.Add((f.DataAdicionado, f.Id, new ItemNovidade(f, 1, f.Titulo)));

        // Mesmo agrupamento da tela: SerieChave (ou o nome cru, se não houver chave).
        foreach (var serie in novos.Where(f => f.EhEpisodio).GroupBy(f => f.SerieChave ?? f.Serie ?? ""))
        {
            var episodios = serie.ToList();
            var maisNovo = episodios
                .OrderByDescending(f => f.Temporada ?? 0).ThenByDescending(f => f.Episodio ?? 0)
                .ThenByDescending(f => f.DataAdicionado).ThenByDescending(f => f.Id)
                .First();
            var nome = MediaNomeParser.NomeExibicao(serie.Key.Length > 0 ? serie.Key : null, NomeMaisFrequente(episodios));
            var rotuloEp = RotuloDoEpisodio(maisNovo);
            var rotulo = episodios.Count > 1 ? $"{nome} · +{episodios.Count} episódios novos"
                : rotuloEp.Length > 0 ? $"{nome} · {rotuloEp}"
                : nome;
            cartoes.Add((episodios.Max(f => f.DataAdicionado), maisNovo.Id, new ItemNovidade(maisNovo, episodios.Count, rotulo)));
        }

        return cartoes.OrderByDescending(c => c.Quando).ThenByDescending(c => c.Id).Take(limite).Select(c => c.Item).ToList();
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
