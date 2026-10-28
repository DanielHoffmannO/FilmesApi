using System.Text;
using System.Text.RegularExpressions;

namespace FilmesApi.Services;

public static partial class MediaNomeParser
{
    // Projeto roda com InvariantGlobalization=true (ver .csproj) — sem ICU, string.Normalize()
    // e CharUnicodeInfo não funcionam de forma confiável (passa no teste, que roda com ICU
    // completo no SDK, mas falha silenciosamente na imagem publicada). Por isso a troca de
    // acento é uma tabela ordinal explícita, igual ao resto do parser já faz.
    internal static char SemAcentoMinuscula(char c) => c switch
    {
        'á' or 'à' or 'â' or 'ã' or 'ä' or 'Á' or 'À' or 'Â' or 'Ã' or 'Ä' => 'a',
        'é' or 'è' or 'ê' or 'ë' or 'É' or 'È' or 'Ê' or 'Ë' => 'e',
        'í' or 'ì' or 'î' or 'ï' or 'Í' or 'Ì' or 'Î' or 'Ï' => 'i',
        'ó' or 'ò' or 'ô' or 'õ' or 'ö' or 'Ó' or 'Ò' or 'Ô' or 'Õ' or 'Ö' => 'o',
        'ú' or 'ù' or 'û' or 'ü' or 'Ú' or 'Ù' or 'Û' or 'Ü' => 'u',
        'ç' or 'Ç' => 'c',
        'ñ' or 'Ñ' => 'n',
        'ý' or 'Ý' or 'ÿ' => 'y',
        >= 'A' and <= 'Z' => (char)(c + 32),
        _ => c,
    };

    // Apelido conhecido -> chave canônica. Acento/maiúscula sozinhos não resolvem "The Vampire
    // Diaries" == "Diários de Um Vampiro" == "T V D" (temporadas da mesma série vieram de 3
    // fontes com nome em idioma/abreviação diferentes) — são palavras diferentes, não só
    // grafia diferente. Lista pequena e explícita, cresce sob demanda quando aparecer outro
    // caso assim na prática; as chaves aqui já estão no formato pós-normalização (minúsculo,
    // sem acento).
    private static readonly Dictionary<string, string> ApelidosPadrao = new()
    {
        ["the vampire diaries"] = "diarios de um vampiro",
        ["t v d"] = "diarios de um vampiro",
        // "DDUVT08EP13" -> prefixo "DDUV" (ver ReTEpColado) -- iniciais de "Diários De Um Vampiro".
        ["dduv"] = "diarios de um vampiro",
        // "Hora da Aventura" (grafia de release, preposição errada) vs "Hora de Aventura"
        // (título oficial BR de Adventure Time) — mesma série, palavra diferente.
        ["hora da aventura"] = "hora de aventura",
        // "FS.03x01.720p.Dub-TorrentDosFilmesHD": FS = Família Soprano, a T3 de The Sopranos
        // (T1/T2 vêm em pastas com o nome da série). A chave é a do nome cru da pasta.
        ["fs 03 720p dub-torrentdosfilmeshd"] = "the sopranos",
        // "Ataque dos Titãs" (pasta de T1-T3 em português, "AoTDublado01x01") vs "Attack on Titan"
        // (releases CR WEB-DL de T3/T4): mesma série, o catálogo mostrava duas.
        ["attack on titan"] = "ataque dos titas",
    };

    // Nome de EXIBIÇÃO quando o mais frequente nas pastas sai feio: sem acento ("Alem da
    // Imaginação"), tudo maiúsculo ("FUTURAMA"), sem hífen. Só muda o que a tela mostra -- o
    // agrupamento continua pela chave, e nenhuma pasta no disco é renomeada (quebraria torrent).
    // Vale pra série e pra franquia (a chave de franquia vem sem o prefixo "franquia:").
    private static readonly Dictionary<string, string> NomesPadrao = new()
    {
        ["alem da imaginacao"] = "Além da Imaginação",
        // Episódios [1x01] Shatterday, Chameleon, Kentucky Rye...: o revival de 1985, outra série
        // que a clássica de cima (o título em português é o mesmo, por isso o ano).
        ["the twilight zone"] = "Além da Imaginação (1985)",
        ["futurama"] = "Futurama",
        ["homem aranha"] = "Homem-Aranha",
        // Títulos oficiais no Brasil (a pasta/arquivo vem com o nome original em inglês):
        ["the sopranos"] = "Família Soprano",
        ["severance"] = "Ruptura",
        ["baby reindeer"] = "Bebê Rena",
        ["hora de aventura"] = "Hora de Aventura",
    };

    // O que vale agora: o padrão embutido acima + o que o /data/nomes.json somar/sobrescrever (ver
    // NomesPersonalizadosService). Trocado por inteiro (referência volátil) — leitura nunca vê
    // dicionário pela metade.
    private static volatile Dictionary<string, string> _apelidos = ApelidosPadrao;
    private static volatile Dictionary<string, string> _nomes = NomesPadrao;
    private static volatile Regex[] _ignorar = [];
    private static volatile HashSet<string> _forcarFilme = [];
    private static int _versaoNomes;

    /// <summary>Muda a cada troca de <c>nomes.json</c> — quem guarda classificação já calculada
    /// (<see cref="ClassificacaoCache"/>) usa isso pra saber que tem que refazer.</summary>
    internal static int VersaoNomes => Volatile.Read(ref _versaoNomes);

    /// <summary>Padrão embutido + entradas do arquivo. As chaves (dos dois dicionários) e os valores de
    /// <paramref name="apelidos"/> (chaves canônicas) entram normalizados — acento/maiúscula/espaço
    /// não importam pra quem edita o arquivo; os valores de <paramref name="nomes"/> ficam como escritos.
    /// Entrada do arquivo com a mesma chave de uma padrão a substitui. Pura (não mexe no estado global).</summary>
    internal static (Dictionary<string, string> Apelidos, Dictionary<string, string> Nomes) Mesclar(
        IReadOnlyDictionary<string, string>? apelidos, IReadOnlyDictionary<string, string>? nomes)
    {
        var a = new Dictionary<string, string>(ApelidosPadrao);
        foreach (var (k, v) in apelidos ?? new Dictionary<string, string>())
        {
            var chave = NormalizarAcentoCase(k);
            var canonica = NormalizarAcentoCase(v);
            if (chave.Length > 0 && canonica.Length > 0) a[chave] = canonica;
        }
        var n = new Dictionary<string, string>(NomesPadrao);
        foreach (var (k, v) in nomes ?? new Dictionary<string, string>())
        {
            var chave = NormalizarAcentoCase(k);
            if (chave.Length > 0 && !string.IsNullOrWhiteSpace(v)) n[chave] = v.Trim();
        }
        return (a, n);
    }

    /// <summary>Passa a valer o padrão + o arquivo (null = só o padrão, sem ignorar nem tipos).</summary>
    internal static void DefinirNomesPersonalizados(NomesArquivo? arquivo)
    {
        var (a, n) = Mesclar(arquivo?.Apelidos, arquivo?.Nomes);
        _apelidos = a;
        _nomes = n;
        _ignorar = (arquivo?.Ignorar ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => GlobCaminho.ParaRegex(g.Trim())).ToArray();
        _forcarFilme = (arquivo?.Tipos ?? new Dictionary<string, string>())
            .Where(kv => kv.Value == NomesArquivo.TipoFilme)
            .Select(kv => NormalizarAcentoCase(kv.Key))
            .Where(k => k.Length > 0)
            .ToHashSet();
        Interlocked.Increment(ref _versaoNomes);
    }

    /// <summary>O caminho relativo bate com algum padrão de <c>ignorar</c> do <c>nomes.json</c>? Aí o arquivo some
    /// do catálogo, do scan e do diagnóstico (mas continua no disco).</summary>
    public static bool Ignorado(string? caminhoRelativo) =>
        caminhoRelativo is not null && Array.Exists(_ignorar, r => r.IsMatch(caminhoRelativo));

    public static string NomeExibicao(string? chave, string nomePadrao) =>
        chave is not null && _nomes.TryGetValue(chave, out var nome) ? nome : nomePadrao;

    /// <summary>Texto da busca/comparação: minúsculo, sem acento, espaços colapsados — "Família" e
    /// "familia", "Espiãs" e "espias" ficam iguais.</summary>
    public static string NormalizarBusca(string s) => NormalizarAcentoCase(s);

    private static string NormalizarAcentoCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(SemAcentoMinuscula(c));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
