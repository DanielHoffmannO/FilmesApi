using System.Text.RegularExpressions;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>"Dublado": o arquivo tem áudio em português que NÃO é a dublagem por IA (<see cref="FilmeResponse.Dublado"/>). Lógica pura —
/// a descoberta pelo ffprobe mora em <see cref="DubladoIaService"/> (guardada em <see cref="AnaliseDeDublagem.AudioPtNaoIa"/>); aqui ficam a
/// leitura das faixas, a dica pelo nome do arquivo (usada só enquanto a análise não rodou), o filtro <c>dublagem=</c> do catálogo e a palavra
/// "dublado" digitada na busca.</summary>
public static partial class DublagemEmPortugues
{
    // ─── valores do parâmetro dublagem= de /api/filmes/tela ───
    public const string Qualquer = "qualquer";   // Dublado ou DubladoIa
    public const string Dublado = "dublado";     // só Dublado (áudio em português que não é IA)
    public const string Ia = "ia";               // só DubladoIa

    /// <summary>Lê as faixas do ffprobe: alguma em português que não é a dublagem por IA = <see cref="AudioPortugues.Sim"/>; senão, alguma
    /// sem idioma informado (vazio, "und"…) = <see cref="AudioPortugues.Indefinido"/> (não dá pra afirmar nada); senão, <see cref="AudioPortugues.Nao"/>.
    /// Faixa sem idioma NUNCA conta como português por si só.</summary>
    internal static AudioPortugues ClassificarAudios(IEnumerable<FaixaAudio> audios)
    {
        var semIdioma = false;
        foreach (var a in audios)
        {
            if (EscolhaAudio.EhDubladoIa(a)) continue;
            if (EscolhaAudio.NormalizarIdioma(a.Idioma) == "por") return AudioPortugues.Sim;
            if (IdiomaNaoInformado(a.Idioma)) semIdioma = true;
        }
        return semIdioma ? AudioPortugues.Indefinido : AudioPortugues.Nao;
    }

    private static bool IdiomaNaoInformado(string? idioma) =>
        string.IsNullOrWhiteSpace(idioma) || idioma.Trim().ToLowerInvariant() is "und" or "unk" or "mul";

    /// <summary>O <c>Dublado</c> final de um arquivo: a análise por probe, quando existe e é conclusiva, manda; senão (ainda não analisado, ou
    /// faixas sem idioma) vale a dica do nome.</summary>
    internal static bool Resolver(AudioPortugues? probe, string? caminho) => probe switch
    {
        AudioPortugues.Sim => true,
        AudioPortugues.Nao => false,
        _ => DicaNoNome(caminho),
    };

    // "dual", "dual audio/áudio", "dublado/a/os/as", "dublagem", "dubl", "nacional", "pt-br"/"ptbr", "português" — palavra inteira, sem
    // acento/caixa (o texto já chega normalizado). "Dualidade", "Dublin", "Internacional" e "Legendado" não casam.
    [GeneratedRegex(@"(?<![a-z0-9])(?:dual(?:[ ._\-]?audio)?|dubl(?:ad[oa]s?|agem|agens)?|nacional|pt[ ._\-]?br|portugues)(?![a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex ReMarcaDePortugues();

    // "Legendado PT-BR", "Legendas em português", "Subs PT-BR", "Leg.ptbr": o português ali é da legenda, não do áudio.
    [GeneratedRegex(@"(?<![a-z0-9])(?:legendad[oa]s?|legendas?|subs?|subtitles?|leg)[ ._\-\[\(]*(?:em[ ._\-]+)?(?:pt[ ._\-]?br|portugues)(?![a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex ReLegendaEmPortugues();

    /// <summary>O caminho (pasta + nome do arquivo) diz que é dublado/dual áudio/nacional/PT-BR? Só uma dica, usada enquanto o ffprobe ainda
    /// não olhou o arquivo.</summary>
    public static bool DicaNoNome(string? caminho)
    {
        if (string.IsNullOrEmpty(caminho)) return false;
        var buf = new char[caminho.Length];
        for (var i = 0; i < buf.Length; i++) buf[i] = MediaNomeParser.SemAcentoMinuscula(caminho[i]);   // tabela ordinal: o projeto roda sem ICU
        var texto = ReLegendaEmPortugues().Replace(new string(buf), " ");
        return ReMarcaDePortugues().IsMatch(texto);
    }

    /// <summary>O filtro do parâmetro <c>dublagem=</c> (<see cref="Qualquer"/>, <see cref="Dublado"/>, <see cref="Ia"/>), ou null (sem filtro)
    /// quando o parâmetro falta ou não é reconhecido.</summary>
    public static Func<FilmeResponse, bool>? Filtro(string? dublagem) => dublagem?.Trim().ToLowerInvariant() switch
    {
        Qualquer => f => f.Dublado || f.DubladoIa,
        Dublado => f => f.Dublado,
        Ia => f => f.DubladoIa,
        _ => null,
    };

    private static readonly HashSet<string> PalavrasDeDublagem = ["dublado", "dublados", "dublada", "dubladas"];

    /// <summary>Na caixa de busca, a palavra "dublado"/"dublados" (e "dublada(s)") vira o filtro <see cref="Qualquer"/> e sai do texto
    /// procurado nos títulos: "dublado" sozinho lista os dublados, "dublado mentalista" só os dublados do Mentalista. Sem nenhuma dessas
    /// palavras devolve a busca intacta e nenhum filtro — a busca por título é a de sempre.</summary>
    public static (string? Busca, string? Dublagem) ExtrairDaBusca(string? busca)
    {
        if (string.IsNullOrWhiteSpace(busca)) return (busca, null);
        var palavras = MediaNomeParser.NormalizarBusca(busca).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resto = palavras.Where(p => !PalavrasDeDublagem.Contains(p)).ToArray();
        if (resto.Length == palavras.Length) return (busca, null);
        return (resto.Length == 0 ? null : string.Join(' ', resto), Qualquer);
    }
}
