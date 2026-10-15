using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>
/// Classificar o catálogo (série/episódio/rótulo/pasta, regex sobre o caminho de cada arquivo)
/// é a parte cara de listar — a consulta ao banco é barata perto disso (medido: ~33 ms dos ~60 ms
/// com 3000 itens, na máquina de desenvolvimento; a placa é bem mais lenta). O resultado só
/// depende de id/caminho/título, que quase nunca mudam, então fica guardado e é reaproveitado
/// enquanto a lista bater EXATAMENTE com a da última vez. Progresso e "assistido" não entram:
/// vêm sempre frescos do banco e são só aplicados por cima. Sem invalidação manual — qualquer
/// diferença (scan, renomeio, título do TMDB) recalcula na chamada seguinte.
/// </summary>
public class ClassificacaoCache
{
    private sealed record Instantaneo(int[] Ids, string?[] Caminhos, string[] Titulos, ClassificacaoMidia[] Resultados);

    private volatile Instantaneo? _atual;

    /// <summary>Quantas vezes o catálogo foi reclassificado de verdade (só pros testes).</summary>
    internal int Recalculos;

    public List<FilmeResponse> Classificar(List<FilmeResponse> lista)
    {
        var atual = _atual;
        if (atual is null || !Confere(atual, lista))
        {
            // Sobre o catálogo INTEIRO — a decisão "essa pasta é antologia" depende de quantos
            // irmãos numerados existem (ver MediaNomeParser.ContarNumeradosPorPasta).
            var contagem = MediaNomeParser.ContarNumeradosPorPasta(lista.Select(f => f.ArquivoPath));
            atual = new Instantaneo(
                [.. lista.Select(f => f.Id)],
                [.. lista.Select(f => f.ArquivoPath)],
                [.. lista.Select(f => f.Titulo)],
                [.. lista.Select(f => MediaNomeParser.Classificar(f.ArquivoPath, f.Titulo, contagem))]);
            _atual = atual;
            Interlocked.Increment(ref Recalculos);
        }

        var resultado = new List<FilmeResponse>(lista.Count);
        for (var i = 0; i < lista.Count; i++) resultado.Add(Aplicar(lista[i], atual.Resultados[i]));
        return resultado;
    }

    private static bool Confere(Instantaneo guardado, List<FilmeResponse> lista)
    {
        if (guardado.Ids.Length != lista.Count) return false;
        for (var i = 0; i < lista.Count; i++)
        {
            var f = lista[i];
            if (guardado.Ids[i] != f.Id || guardado.Titulos[i] != f.Titulo || guardado.Caminhos[i] != f.ArquivoPath)
                return false;
        }
        return true;
    }

    private static FilmeResponse Aplicar(FilmeResponse f, ClassificacaoMidia c) => f with
    {
        EhEpisodio = c.EhEpisodio,
        EhExtra = c.EhExtra,
        Serie = c.Serie,
        SerieChave = c.SerieChave,
        Temporada = c.Temporada,
        Episodio = c.Episodio,
        Rotulo = c.Rotulo,
        Pasta = c.Pasta,
    };
}
