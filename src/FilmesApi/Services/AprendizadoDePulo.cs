namespace FilmesApi.Services;

/// <summary>Regras puras de "o Daniel pulou aqui: era a abertura?": onde extrair o áudio, como validar contra os vizinhos e como procurar o
/// mesmo trecho nos outros episódios. Sem ffmpeg nem banco (recebe as impressões).</summary>
public static class AprendizadoDePulo
{
    /// <summary>Folga extraída antes e depois do trecho pulado (a rajada é imprecisa em alguns segundos).</summary>
    public const double FolgaSegundos = 15;

    /// <summary>Quanto um trecho pode estar deslocado da janela: a janela é curta e o vizinho tem 480 s inteiros.</summary>
    private const double DeslocamentoMaxSegundos = 900;

    /// <summary>A janela de áudio a extrair do episódio: [saída − 15 s, chegada + 15 s].</summary>
    public static (double Inicio, double Duracao) Janela(RajadaDePulo r)
    {
        var inicio = Math.Max(0, r.PosAntes - FolgaSegundos);
        return (inicio, r.PosFinal + FolgaSegundos - inicio);
    }

    /// <summary>A rajada cobre a abertura que o servidor já conhece (≥ metade dela)? Então só confirma — sem reanalisar nada.</summary>
    public static bool CobreAConhecida(RajadaDePulo r, double inicio, double fim) =>
        Math.Min(r.PosFinal, fim) - Math.Max(r.PosAntes, inicio) >= (fim - inicio) / 2;

    /// <summary>Procura, na impressão da janela, trecho comum (≥ <paramref name="minimoSegundos"/>) com a de algum vizinho, que se sobreponha ao
    /// pulado. Devolve o trecho em segundos ABSOLUTOS do episódio (bordas estendidas pelo critério folgado), ou null: era só uma cena pulada.</summary>
    public static TrechoComum? Validar(int[] janela, double inicioDaJanela, RajadaDePulo r, IEnumerable<int[]> vizinhos, double minimoSegundos) =>
        vizinhos.Where(v => v.Length > 0)
            .Select(v => DetectorDeAbertura.Encontrar(janela, v, minimoSegundos, DeslocamentoMaxSegundos))
            .OfType<TrechoComum>()
            .Where(t => Math.Min(r.PosFinal, inicioDaJanela + t.FimA) - Math.Max(r.PosAntes, inicioDaJanela + t.InicioA) >= minimoSegundos)
            .OrderByDescending(t => t.Duracao)
            .Select(t => t with { InicioA = inicioDaJanela + t.InicioA, FimA = inicioDaJanela + t.FimA })
            .FirstOrDefault();

    /// <summary>O trecho validado serve de MODELO: acha o mesmo áudio na impressão de outro episódio (sem o mínimo de 10 s — o Daniel já
    /// confirmou que ali é a abertura; basta cobrir 70 % do modelo e ao menos <paramref name="minimoPuloSegundos"/>). Posições no outro episódio.</summary>
    public static (double Inicio, double Fim)? ProcurarModelo(int[] janela, double inicioDaJanela, TrechoComum validado, int[] outroEpisodio, double minimoPuloSegundos)
    {
        var de = (int)Math.Max(0, (validado.InicioA - inicioDaJanela) / DetectorDeAbertura.SegundosPorItem);
        var ate = Math.Min(janela.Length, (int)Math.Ceiling((validado.FimA - inicioDaJanela) / DetectorDeAbertura.SegundosPorItem));
        if (ate <= de || outroEpisodio.Length == 0) return null;
        var modelo = janela[de..ate];
        var achado = DetectorDeAbertura.Encontrar(modelo, outroEpisodio,
            Math.Max(minimoPuloSegundos, 0.7 * (ate - de) * DetectorDeAbertura.SegundosPorItem), DeslocamentoMaxSegundos);
        return achado is null ? null : (achado.InicioB, achado.FimB);
    }
}
