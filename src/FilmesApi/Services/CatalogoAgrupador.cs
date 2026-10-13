using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Filtro e agrupamento do catálogo pra tela (filme solto / pasta filme+extras /
/// franquia / série). Lógica pura — não toca banco, por isso testável direto.</summary>
public static class CatalogoAgrupador
{
    /// <summary>Filtra (tipo/visto/busca) e agrupa (filme solto / pasta filme+extras / série)
    /// pra tela desenhar direto — função pura, testável sem banco. <c>continuarCandidatos</c>
    /// já vem enriquecido e na ordem certa (mais recente primeiro); esta função só decide se
    /// a seção aparece (tipo e visto neutros) e aplica a busca nela também.</summary>
    public static TelaCatalogoResponse Montar(
        List<FilmeResponse> todos, List<FilmeResponse> continuarCandidatos,
        string tipo, string visto, string? busca)
    {
        var q = string.IsNullOrWhiteSpace(busca) ? null : busca.Trim().ToLowerInvariant();
        bool PassaBusca(FilmeResponse f) =>
            q is null || (f.Titulo + " " + (f.Serie ?? "") + " " + (f.Pasta ?? "")).ToLowerInvariant().Contains(q);

        var continuarAssistindo = tipo == "all" && visto == "all"
            ? continuarCandidatos.Where(PassaBusca).ToList()
            : [];

        bool PassaFiltro(FilmeResponse f) =>
            (visto != "assistido" || f.Assistido) &&
            (visto != "nao-assistido" || !f.Assistido) &&
            (tipo != "filme" || !f.EhEpisodio) &&
            (tipo != "serie" || f.EhEpisodio) &&
            PassaBusca(f);

        // Contagem por pasta na lista INTEIRA (não na filtrada) — senão a busca "achataria"
        // uma pasta de filme+extras só por esconder temporariamente os outros arquivos dela.
        var porPasta = todos.GroupBy(f => f.Pasta).ToDictionary(g => g.Key, g => g.Count());

        var filmesSoltos = new List<FilmeResponse>();
        var pastasFilme = new Dictionary<string, List<FilmeResponse>>();
        // Nomes: conta ocorrência de cada Serie "bruto" dentro do grupo -- decide o nome de
        // exibição pelo MAIS FREQUENTE (não o primeiro visto), senão um apelido/pasta de
        // release feia com poucos episódios podia "vencer" a exibição de um grupo com
        // centenas de episódios só por coincidência de ordem (ver ApelidosSerie).
        var series = new Dictionary<string, (Dictionary<string, int> Nomes, List<FilmeResponse> Itens)>();

        foreach (var f in todos.Where(PassaFiltro))
        {
            if (f.EhEpisodio)
            {
                var chave = f.SerieChave ?? f.Serie ?? "";
                if (!series.TryGetValue(chave, out var g)) { g = ([], []); series[chave] = g; }
                var nome = f.Serie ?? "";
                g.Nomes[nome] = g.Nomes.GetValueOrDefault(nome) + 1;
                g.Itens.Add(f);
                continue;
            }
            if (f.Pasta != "Sem pasta" && porPasta.GetValueOrDefault(f.Pasta) > 1)
            {
                if (!pastasFilme.TryGetValue(f.Pasta, out var lista)) { lista = []; pastasFilme[f.Pasta] = lista; }
                lista.Add(f);
            }
            // Extra sozinho (sem pasta, ou pasta que não bateu o limiar de 2+) não tem filme
            // nenhum pra "pertencer" — não faz sentido mostrar um trailer/sample como se fosse
            // um filme à parte.
            else if (!f.EhExtra) filmesSoltos.Add(f);
        }

        // pasta com 1 filme "de verdade" + resto extra (trailer/sample) -> mostra o filme
        // direto. Pasta com 0 filme de verdade (ex.: todo o conteúdo real da pasta virou
        // episódio de série — sobrou só um promo/propaganda do uploader) -> descarta a pasta
        // inteira, não tem nada de real pra mostrar.
        foreach (var pasta in pastasFilme.Keys.ToList())
        {
            var principais = pastasFilme[pasta].Where(f => !f.EhExtra).ToList();
            if (principais.Count == 1) filmesSoltos.Add(principais[0]);
            if (principais.Count <= 1) pastasFilme.Remove(pasta);
        }

        // Franquia por título: junta filmes/pastas da MESMA franquia que moram em pastas sem
        // relação nenhuma (sem pasta-mãe "Trilogia .../" ligando elas -- RePastaColecao não
        // cobre esse caso). Cobre tanto "cada filme solto na sua pasta" (Toy Story 1/2/3, cada
        // um sozinho) quanto "1 filme solto + outros já numa pasta de verdade juntos"
        // (Homem-Aranha 1 sozinho; Homem-Aranha 2 e 3 já juntos em "TRILOGIA.../", pasta real
        // com 2 arquivos). Só corta um marcador de SEQUÊNCIA do título (número/romano no
        // fim) -- não é correspondência de nome parecido (isso juntaria "Poder" com "Poder
        // Absoluto" à toa).
        var candidatosFranquia = new List<(string Chave, string PastaOrigem, List<FilmeResponse> Itens)>();
        foreach (var f in filmesSoltos)
        {
            var chave = MediaNomeParser.ChaveBaseFranquia(f.Titulo);
            if (!string.IsNullOrEmpty(chave)) candidatosFranquia.Add((chave, "", [f]));
        }
        foreach (var (pastaChave, itensPasta) in pastasFilme)
        {
            // Só considera a pasta inteira pra franquia se TODOS os itens dela concordam na
            // mesma chave -- uma pasta "filme + extras" onde os títulos não convergem pra uma
            // franquia só não entra (ex.: 2 cortes/versões do mesmo filme com títulos diferentes).
            var chavesDaPasta = itensPasta
                .Select(f => MediaNomeParser.ChaveBaseFranquia(f.Titulo))
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .ToList();
            if (chavesDaPasta.Count == 1) candidatosFranquia.Add((chavesDaPasta[0]!, pastaChave, itensPasta));
        }

        var gruposFranquia = new List<PastaAgrupada>();
        var pastasDeFranquiaParaRemover = new List<string>();
        var idsSoltosDeFranquiaParaRemover = new HashSet<int>();
        foreach (var grupo in candidatosFranquia.GroupBy(c => c.Chave).Where(g => g.Count() > 1))
        {
            var itensDoGrupo = grupo.SelectMany(c => c.Itens).ToList();
            var nomesBase = new Dictionary<string, int>();
            foreach (var f in itensDoGrupo)
            {
                var nome = MediaNomeParser.NomeBaseFranquia(f.Titulo);
                nomesBase[nome] = nomesBase.GetValueOrDefault(nome) + 1;
            }
            var nomeExibicao = nomesBase
                .OrderByDescending(nc => nc.Value)
                .ThenBy(nc => nc.Key, StringComparer.OrdinalIgnoreCase)
                .First().Key;
            var itensOrdenados = itensDoGrupo
                .OrderBy(f => f.AnoLancamento ?? int.MaxValue)
                .ThenBy(f => f.Titulo, StringComparer.OrdinalIgnoreCase)
                .ToList();
            gruposFranquia.Add(new PastaAgrupada("franquia:" + grupo.Key, nomeExibicao, itensOrdenados));

            foreach (var c in grupo)
            {
                if (c.PastaOrigem.Length > 0) pastasDeFranquiaParaRemover.Add(c.PastaOrigem);
                else idsSoltosDeFranquiaParaRemover.Add(c.Itens[0].Id);
            }
        }
        foreach (var p in pastasDeFranquiaParaRemover) pastasFilme.Remove(p);
        if (idsSoltosDeFranquiaParaRemover.Count > 0)
            filmesSoltos.RemoveAll(f => idsSoltosDeFranquiaParaRemover.Contains(f.Id));

        filmesSoltos.Sort((a, b) => string.Compare(a.Titulo, b.Titulo, StringComparison.OrdinalIgnoreCase));

        foreach (var lista in series.Values.Select(v => v.Itens))
            lista.Sort((a, b) =>
            {
                var c = (a.Temporada ?? 0).CompareTo(b.Temporada ?? 0);
                if (c != 0) return c;
                c = (a.Episodio ?? 0).CompareTo(b.Episodio ?? 0);
                return c != 0 ? c : string.Compare(a.Titulo, b.Titulo, StringComparison.OrdinalIgnoreCase);
            });

        var pastasOrdenadas = pastasFilme
            .Select(kv => new PastaAgrupada(kv.Key, MediaNomeParser.NomePastaExibicao(kv.Key), kv.Value))
            .Concat(gruposFranquia)
            .OrderBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Nome de exibição = o mais frequente no grupo; empate resolvido alfabeticamente,
        // só pra dar um resultado determinístico (não devia acontecer na prática).
        string NomeMaisFrequente(Dictionary<string, int> nomes) => nomes
            .OrderByDescending(nc => nc.Value)
            .ThenBy(nc => nc.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;

        var seriesOrdenadas = series
            .Select(kv => new SerieAgrupada(kv.Key, NomeMaisFrequente(kv.Value.Nomes), kv.Value.Itens))
            .OrderBy(s => s.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TelaCatalogoResponse(continuarAssistindo, filmesSoltos, pastasOrdenadas, seriesOrdenadas, todos.Count == 0);
    }
}
