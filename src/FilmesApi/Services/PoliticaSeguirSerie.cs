namespace FilmesApi.Services;

/// <summary>Um episódio de uma série seguida, já na ordem (temporada, episódio). <c>EmAndamento</c> = tem posição salva e não foi terminado.</summary>
public record EpisodioSeguido(int Id, bool Assistido, DateTime? AssistidoEm, bool EmAndamento);

/// <summary>
/// As regras puras de "Seguir série no cache" (sem I/O): qual janela de episódios o servidor mantém pronta e quem ainda fica protegido depois de assistido.
/// <list type="bullet">
/// <item>Janela: os <c>N</c> primeiros episódios NÃO assistidos a partir do atual — depois do último assistido, mais o que estiver em andamento.
/// Episódio novo que chega no scan entra sozinho se cair dentro dos próximos N.</item>
/// <item>Assistido sai do "seguido" <c>liberaApos</c> depois de ter sido assistido (um dia por padrão): até lá a cópia continua protegida (quem revê
/// o episódio não espera de novo); depois vira cache comum e a limpeza normal despeja.</item>
/// </list>
/// </summary>
public static class PoliticaSeguirSerie
{
    public const int JanelaPadrao = 5;
    public static readonly TimeSpan LiberaAposPadrao = TimeSpan.FromHours(24);

    /// <summary>Os ids da janela, na ordem de exibição. <paramref name="episodios"/> já vem ordenado.</summary>
    public static IReadOnlyList<int> Janela(IReadOnlyList<EpisodioSeguido> episodios, int n)
    {
        if (n <= 0) return [];
        var ultimoAssistido = -1;
        for (var i = 0; i < episodios.Count; i++) if (episodios[i].Assistido) ultimoAssistido = i;
        var janela = new List<int>(n);
        for (var i = 0; i < episodios.Count && janela.Count < n; i++)
        {
            var e = episodios[i];
            if (!e.Assistido && (i > ultimoAssistido || e.EmAndamento)) janela.Add(e.Id);
        }
        return janela;
    }

    /// <summary>Assistidos há menos de <paramref name="liberaApos"/>: continuam protegidos. Sem data de quando foi assistido (linha antiga), já está liberado.</summary>
    public static IReadOnlyList<int> RecemAssistidos(IReadOnlyList<EpisodioSeguido> episodios, DateTime agoraUtc, TimeSpan liberaApos) =>
        [.. episodios.Where(e => e.Assistido && e.AssistidoEm is DateTime quando && agoraUtc - quando < liberaApos).Select(e => e.Id)];
}
