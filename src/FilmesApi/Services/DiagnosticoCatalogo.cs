using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmesApi.Models;

namespace FilmesApi.Services;

public record ArquivoDiag(int Id, string Caminho, long? Bytes);

/// <summary>Episódios que faltam numa temporada; <c>Resumo</c> os compacta: <c>E05, E15, E33, E14–E16</c>.</summary>
public record FaltandoDiag(string Serie, int Temporada, List<int> Episodios, string Resumo);

public record RepetidoDiag(string Serie, int Temporada, int Episodio, List<ArquivoDiag> Arquivos);

public record UmEpisodioDiag(string Serie, int Temporada, int Episodio, ArquivoDiag Arquivo);

/// <summary><c>EntradaIgnorar</c>: o caminho já entre aspas, pronto pra colar na lista <c>ignorar</c> do <c>nomes.json</c>.</summary>
public record SuspeitoDiag(ArquivoDiag Arquivo, string Motivo, string EntradaIgnorar);

public record DiagnosticoResponse(
    int TotalArquivos,
    List<FaltandoDiag> Faltando,
    List<RepetidoDiag> Repetidos,
    List<UmEpisodioDiag> SerieComUmEpisodio,
    List<ArquivoDiag> SoltosQueParecemEpisodio,
    List<SuspeitoDiag> SuspeitosDePropaganda);

/// <summary>
/// O que dá pra descobrir olhando o catálogo inteiro: que episódios faltam, o que está repetido, o que o
/// parser não reconheceu e o que parece propaganda de uploader. Antes isso era um script solto contra
/// <c>/api/filmes/tela</c>. Pura — só recebe a lista classificada (e uma função que mede o tamanho do arquivo,
/// chamada só pro que precisa), então dá pra testar sem disco nem banco.
/// </summary>
public static partial class DiagnosticoCatalogo
{
    private const long LimitePropagandaBytes = 60L * 1024 * 1024;

    // Propaganda de uploader que vem como "vídeo" de poucos MB (os 12 que o dono apagou na mão eram esses).
    [GeneratedRegex(@"1xbet|bludv|comandotorrents|lapumia", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReNomeDePropaganda();

    [GeneratedRegex(@"\bsample\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReSample();

    // O que o parser NÃO reconhece como episódio mas parece um: "Ep05", "E05", "Show - 05 - Titulo", "05 - Titulo".
    [GeneratedRegex(@"(?:^|[\s._\-\[(])(?:E|Ep|Cap|Epi)\.?\s?[0-9]{1,3}\b|\s[-–]\s*[0-9]{1,3}\s*(?:[-–.\[(]|$)|^\s*[0-9]{1,3}\s*[-–.]\s+\D",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReParecePossivelEpisodio();

    /// <summary>Uma série do catálogo: nome de exibição e, por temporada, que arquivos cobrem cada episódio
    /// (arquivo duplo cobre os dois números).</summary>
    private sealed record SerieAgregada(string Nome, SortedDictionary<int, SortedDictionary<int, List<FilmeResponse>>> Temporadas);

    /// <param name="catalogo">Itens já classificados (<see cref="ClassificacaoCache"/>).</param>
    /// <param name="tamanhoDe">Bytes do arquivo (caminho relativo → tamanho; null se não achou). Só é chamada
    /// pros arquivos repetidos e pros suspeitos por nome.</param>
    public static DiagnosticoResponse Analisar(IReadOnlyList<FilmeResponse> catalogo, Func<string, long?> tamanhoDe)
    {
        ArquivoDiag ComTamanho(FilmeResponse f) => new(f.Id, f.ArquivoPath ?? "", tamanhoDe(f.ArquivoPath ?? ""));

        var series = AgruparPorSerie(catalogo);
        return new DiagnosticoResponse(
            catalogo.Count,
            series.SelectMany(Lacunas).ToList(),
            series.SelectMany(s => Repetidos(s, ComTamanho)).ToList(),
            series.Select(UmSoEpisodio).OfType<UmEpisodioDiag>().ToList(),
            SoltosQueParecemEpisodio(catalogo),
            Suspeitos(catalogo, ComTamanho));
    }

    private static List<SerieAgregada> AgruparPorSerie(IReadOnlyList<FilmeResponse> catalogo)
    {
        var porChave = new Dictionary<string, (Dictionary<string, int> Nomes, SortedDictionary<int, SortedDictionary<int, List<FilmeResponse>>> Temporadas)>();
        foreach (var f in catalogo.Where(f => f.EhEpisodio && !f.EhExtra && f.Temporada is not null && f.Episodio is not null))
        {
            var chave = f.SerieChave ?? f.Serie ?? "";
            if (!porChave.TryGetValue(chave, out var serie)) porChave[chave] = serie = ([], []);
            var nome = f.Serie ?? "";
            serie.Nomes[nome] = serie.Nomes.GetValueOrDefault(nome) + 1;

            var (temp, de, ate) = MediaNomeParser.FaixaDeEpisodios(f.ArquivoPath, f.Temporada!.Value, f.Episodio!.Value);
            if (!serie.Temporadas.TryGetValue(temp, out var eps)) serie.Temporadas[temp] = eps = [];
            for (var e = de; e <= ate; e++)
            {
                if (!eps.TryGetValue(e, out var arquivos)) eps[e] = arquivos = [];
                arquivos.Add(f);
            }
        }

        return porChave
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new SerieAgregada(
                MediaNomeParser.NomeExibicao(kv.Key,
                    kv.Value.Nomes.OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase).First().Key),
                kv.Value.Temporadas))
            .ToList();
    }

    /// <summary>Buraco entre o episódio 1 e o maior número que existe na temporada.</summary>
    private static IEnumerable<FaltandoDiag> Lacunas(SerieAgregada serie)
    {
        foreach (var (temp, eps) in serie.Temporadas)
        {
            var falta = Enumerable.Range(1, eps.Keys.Max()).Where(e => !eps.ContainsKey(e)).ToList();
            if (falta.Count > 0) yield return new FaltandoDiag(serie.Nome, temp, falta, Resumir(falta));
        }
    }

    /// <summary>Episódio coberto por 2+ arquivos distintos.</summary>
    private static IEnumerable<RepetidoDiag> Repetidos(SerieAgregada serie, Func<FilmeResponse, ArquivoDiag> comTamanho)
    {
        foreach (var (temp, eps) in serie.Temporadas)
            foreach (var (ep, arquivos) in eps)
            {
                var distintos = arquivos.DistinctBy(a => a.Id).ToList();
                if (distintos.Count > 1) yield return new RepetidoDiag(serie.Nome, temp, ep, distintos.Select(comTamanho).ToList());
            }
    }

    private static UmEpisodioDiag? UmSoEpisodio(SerieAgregada serie)
    {
        if (serie.Temporadas.Sum(t => t.Value.Count) != 1) return null;
        var (temp, eps) = serie.Temporadas.First();
        var (ep, arquivos) = eps.First();
        return new UmEpisodioDiag(serie.Nome, temp, ep, new ArquivoDiag(arquivos[0].Id, arquivos[0].ArquivoPath ?? "", null));
    }

    private static List<ArquivoDiag> SoltosQueParecemEpisodio(IReadOnlyList<FilmeResponse> catalogo) => catalogo
        .Where(f => !f.EhEpisodio && !f.EhExtra && f.Pasta == "Sem pasta" && ReParecePossivelEpisodio().IsMatch(NomeSemExtensao(f)))
        .Select(f => new ArquivoDiag(f.Id, f.ArquivoPath ?? "", null))
        .ToList();

    private static List<SuspeitoDiag> Suspeitos(IReadOnlyList<FilmeResponse> catalogo, Func<FilmeResponse, ArquivoDiag> comTamanho)
    {
        var suspeitos = new List<SuspeitoDiag>();
        foreach (var f in catalogo)
        {
            var nome = f.ArquivoPath ?? "";
            if (ReSample().IsMatch(nome)) { suspeitos.Add(Suspeito(comTamanho(f), "nome tem 'Sample'")); continue; }
            if (!ReNomeDePropaganda().IsMatch(nome)) continue;
            var arquivo = comTamanho(f);
            if (arquivo.Bytes is long b && b < LimitePropagandaBytes)
                suspeitos.Add(Suspeito(arquivo, $"nome de uploader e só {b / 1024 / 1024} MB"));
        }
        return suspeitos;
    }

    private static readonly JsonSerializerOptions JsonLegivel = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Sem escape de glob: um nome com "*" ou "?" casaria mais que ele mesmo (raro em arquivo de vídeo).
    private static SuspeitoDiag Suspeito(ArquivoDiag arquivo, string motivo) =>
        new(arquivo, motivo, JsonSerializer.Serialize(arquivo.Caminho, JsonLegivel));

    private static string NomeSemExtensao(FilmeResponse f) => Path.GetFileNameWithoutExtension(f.ArquivoPath ?? "");

    /// <summary>[5, 15, 33, 14, 15, 16] → "E05, E14–E16, E33": sequências viram intervalo.</summary>
    internal static string Resumir(IEnumerable<int> numeros)
    {
        var partes = new List<string>();
        var lista = numeros.Distinct().Order().ToList();
        for (var i = 0; i < lista.Count;)
        {
            var j = i;
            while (j + 1 < lista.Count && lista[j + 1] == lista[j] + 1) j++;
            partes.Add(lista[j] > lista[i] ? $"E{lista[i]:00}–E{lista[j]:00}" : $"E{lista[i]:00}");
            i = j + 1;
        }
        return string.Join(", ", partes);
    }
}
