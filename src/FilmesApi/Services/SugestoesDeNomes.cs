using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Uma correção proposta pro <c>nomes.json</c>: <c>Secao</c> é <c>"apelidos"</c> (juntar duas séries) ou
/// <c>"nomes"</c> (nome de exibição); <c>Chave</c> → <c>Valor</c> é a entrada pronta.</summary>
public record SugestaoNome(string Secao, string Serie, string Motivo, string Chave, string Valor);

/// <summary>
/// Olha as séries do catálogo e propõe correções de nome: (1) a mesma série sob dois nomes ("O Mentalista" /
/// "Mentalista", "Hora de Aventura" / "Hora de Aventuras") — vira um <c>apelido</c> do nome com menos episódios pro
/// com mais; (2) nome de exibição que parece de release (TUDO MAIÚSCULO, tudo minúsculo) — vira um <c>nome</c> em
/// caixa de título. Só sugere: quem decide é a pessoa (botão aplicar ou copiar). Pura, sem disco nem banco.
/// </summary>
public static class SugestoesDeNomes
{
    private static readonly HashSet<string> Palavrinhas = new(["o", "a", "os", "as", "the", "de", "da", "do", "dos", "das", "e", "and", "of"]);

    private sealed record Serie(string Chave, string Nome, int Episodios);

    public static List<SugestaoNome> Analisar(IReadOnlyList<FilmeResponse> catalogo)
    {
        var series = catalogo
            .Where(f => f.EhEpisodio && !f.EhExtra && !string.IsNullOrEmpty(f.SerieChave))
            .GroupBy(f => f.SerieChave!)
            .Select(g => new Serie(g.Key,
                MediaNomeParser.NomeExibicao(g.Key, g.GroupBy(f => f.Serie ?? "").OrderByDescending(n => n.Count()).First().Key),
                g.Count()))
            .OrderBy(s => s.Chave, StringComparer.Ordinal)
            .ToList();

        var sugestoes = new List<SugestaoNome>();
        sugestoes.AddRange(MesmaSerie(series));
        sugestoes.AddRange(series.Select(NomeFeio).OfType<SugestaoNome>());
        return sugestoes;
    }

    private static IEnumerable<SugestaoNome> MesmaSerie(List<Serie> series)
    {
        for (var i = 0; i < series.Count; i++)
            for (var j = i + 1; j < series.Count; j++)
            {
                var (a, b) = (series[i], series[j]);
                if (!ParecemASerieMesma(a.Chave, b.Chave)) continue;
                var (menor, maior) = a.Episodios <= b.Episodios ? (a, b) : (b, a);
                yield return new SugestaoNome("apelidos", menor.Nome,
                    $"parece a mesma série que \"{maior.Nome}\" ({menor.Episodios} × {maior.Episodios} episódios)", menor.Chave, maior.Chave);
            }
    }

    /// <summary>Iguais sem artigos/preposições ("o mentalista" = "mentalista"), ou a 1–2 letras de distância em nome
    /// comprido ("hora de aventura" ~ "hora de aventuras"). Dígitos têm que bater: "serie 2" não é "serie 3".</summary>
    internal static bool ParecemASerieMesma(string a, string b)
    {
        var (ta, tb) = (SemPalavrinhas(a), SemPalavrinhas(b));
        if (ta.Length == 0 || tb.Length == 0) return false;
        if (ta == tb) return true;
        if (new string(ta.Where(char.IsDigit).ToArray()) != new string(tb.Where(char.IsDigit).ToArray())) return false;
        return Math.Min(ta.Length, tb.Length) >= 8 && Distancia(ta, tb) <= 2;
    }

    private static string SemPalavrinhas(string chave) =>
        string.Join(' ', chave.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !Palavrinhas.Contains(p)));

    private static int Distancia(string a, string b)
    {
        var anterior = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var atual = new int[b.Length + 1];
            atual[0] = i;
            for (var j = 1; j <= b.Length; j++)
                atual[j] = Math.Min(Math.Min(atual[j - 1], anterior[j]) + 1, anterior[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            anterior = atual;
        }
        return anterior[b.Length];
    }

    private static SugestaoNome? NomeFeio(Serie s)
    {
        var letras = s.Nome.Where(char.IsLetter).ToList();
        if (letras.Count < 5) return null;   // sigla curta (CSI, NCIS) é assim mesmo
        var motivo = letras.All(char.IsUpper) ? "nome todo em maiúsculas"
            : letras.All(char.IsLower) ? "nome todo em minúsculas" : null;
        if (motivo is null) return null;
        return new SugestaoNome("nomes", s.Nome, motivo, s.Chave, CaixaDeTitulo(s.Nome));
    }

    internal static string CaixaDeTitulo(string nome)
    {
        var palavras = nome.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', palavras.Select((p, i) =>
            i > 0 && Palavrinhas.Contains(p) ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
