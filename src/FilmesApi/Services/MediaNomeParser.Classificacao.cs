using System.Text.RegularExpressions;

namespace FilmesApi.Services;

public static partial class MediaNomeParser
{
    /// <summary>Chave de agrupamento tolerante a acento/maiúscula/espaço — mesma série
    /// escrita de jeitos ligeiramente diferentes em pastas de fontes diferentes ("Diários de
    /// Um Vampiro" / "Diarios de um vampiro" / "Diários de um Vampiro") cai na mesma chave.
    /// Também resolve apelidos conhecidos (<c>ApelidosPadrao</c> + <c>nomes.json</c>). Só serve pra
    /// comparar/agrupar — quem exibe usa <see cref="ChaveSerie"/> sem alterar.</summary>
    public static string? ChaveAgrupamento(string? serie)
    {
        if (string.IsNullOrEmpty(serie)) return serie;
        var chave = NormalizarAcentoCase(serie);
        return _apelidos.GetValueOrDefault(chave, chave);
    }

    /// <summary>Título sem o marcador de sequência do fim (ver <see cref="ReSequenciaFranquia"/>),
    /// preservando acento/maiúscula — pra exibir.</summary>
    public static string NomeBaseFranquia(string titulo) => ReSequenciaFranquia().Replace(titulo, "").TrimEnd();

    /// <summary>Chave de agrupamento de franquia por título — tolerante a acento/maiúscula
    /// (mesma normalização de <see cref="ChaveAgrupamento"/>), mais o corte do marcador de
    /// sequência (<see cref="NomeBaseFranquia"/>). Une filmes de uma franquia que moram em
    /// pastas TOTALMENTE separadas, sem pasta-mãe nenhuma ligando eles — diferente de
    /// <see cref="RePastaColecao"/>, que só resolve o caso de cada filme numa subpasta DENTRO
    /// de um wrapper "Trilogia .../". Só corta número/romano de sequência — não é
    /// correspondência de nome parecido (isso juntaria "Poder" com "Poder Absoluto" à toa);
    /// sem marcador de sequência no fim, o título passa direto e só bate com uma cópia
    /// idêntica dele mesmo.</summary>
    public static string? ChaveBaseFranquia(string? titulo) =>
        string.IsNullOrEmpty(titulo) ? titulo : NormalizarAcentoCase(NomeBaseFranquia(titulo));

    /// <summary>Nome de exibição de uma pasta "filme + extras" (trailer/sample junto) — a
    /// pasta pode estar em qualquer profundidade ("extra/Nome Feio"), só o último segmento
    /// importa pra exibir. Tira ponto/underscore, propaganda de site entre colchetes,
    /// qualidade/codec (<see cref="ReRuido"/>) e assinatura de release no fim. Só pra
    /// exibir — o agrupamento continua pelo caminho relativo completo (<c>Pasta</c> bruto),
    /// que já é único por pasta real.</summary>
    public static string NomePastaExibicao(string pasta)
    {
        var i = pasta.LastIndexOf('/');
        var basename = i >= 0 ? pasta[(i + 1)..] : pasta;

        // ReAssinaturaRelease só entra se a pasta já tem outro indício de nome de release
        // (ponto/underscore separando tokens, ou tag de qualidade/codec) -- sem isso, um nome
        // limpo tipo "X-MEN" ou "K-PAX" (comuns de verdade) teria o final maiúsculo cortado
        // igual a uma assinatura de grupo de release, virando "X"/"K" na tela.
        var pareceRelease = basename.Contains('.') || basename.Contains('_') || ReRuido().IsMatch(basename);

        var nome = RePontos().Replace(basename, " ");
        nome = ReAnuncioSite().Replace(nome, " ");
        nome = ReDominioSolto().Replace(nome, " ");
        nome = ReRuido().Replace(nome, " ");
        nome = ReDominioSemPontos().Replace(nome, " ");
        nome = RePrefixoColecao().Replace(nome.TrimStart(), "");
        nome = ReIntervaloAnos().Replace(nome, " ");
        if (pareceRelease) nome = ReAssinaturaRelease().Replace(nome, "");
        nome = Regex.Replace(nome, @"[\[\]()]+", " ");
        nome = Regex.Replace(nome, @"\s+", " ").Trim(' ', '-', '–', '—');

        return nome.Length > 0 ? nome : basename;
    }

    /// <summary>Classificação completa — o que o <c>FilmeResponse</c> entrega pras telas.
    /// <paramref name="contagemNumeradosPorPasta"/> vem de <see cref="ContarNumeradosPorPasta"/>
    /// calculado sobre o catálogo inteiro (ver <c>FilmeService.ListarAsync</c>) — sem isso,
    /// pasta de antologia sem marcador de temporada nunca vira episódio.</summary>
    public static ClassificacaoMidia Classificar(
        string? arquivoPath, string titulo, IReadOnlyDictionary<string, int>? contagemNumeradosPorPasta = null)
    {
        var pasta = PastaDe(arquivoPath);
        var arquivosNumerados = contagemNumeradosPorPasta?.GetValueOrDefault(pasta) ?? 0;
        var ordem = OrdemEpisodio(arquivoPath, arquivosNumerados);
        if (ordem is not (int temp, int ep))
            return new ClassificacaoMidia(false, EhExtra(arquivoPath), null, null, null, null, titulo, pasta);

        var serie = ChaveSerie(arquivoPath);
        // "tipos": a pessoa já disse que essa "série" é filme (um SxxExx falso no nome enganou o parser)
        if (_forcarFilme.Contains(ChaveAgrupamento(serie) ?? ""))
            return new ClassificacaoMidia(false, EhExtra(arquivoPath), null, null, null, null, titulo, pasta);

        return new ClassificacaoMidia(
            EhEpisodio: true,
            EhExtra: EhExtra(arquivoPath),
            Serie: serie,
            SerieChave: ChaveAgrupamento(serie),
            Temporada: temp,
            Episodio: ep,
            Rotulo: MontarRotulo(SemExtensao(NomeArquivo(arquivoPath)), temp, ep),
            Pasta: pasta,
            TituloEpisodio: TituloDoEpisodio(SemExtensao(NomeArquivo(arquivoPath))));
    }
}
