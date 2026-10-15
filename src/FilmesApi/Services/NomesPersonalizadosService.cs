using System.Text.Json;

namespace FilmesApi.Services;

/// <summary>
/// Apelidos de série e nomes de exibição num arquivo editável (<c>NomesPath</c>, padrão
/// <c>/data/nomes.json</c>) em vez de dicionário no C# — cada série nova com nome feio custava
/// build + deploy. Formato (tudo opcional):
/// <code>{ "apelidos": { "attack on titan": "ataque dos titas" }, "nomes": { "ataque dos titas": "Ataque dos Titãs" },
///   "ignorar": ["extra/Laranja.Mecanica*/1XBET*", "**/Sample/*"], "tipos": { "cabo do medo": "filme" } }</code>
/// <c>ignorar</c>: glob no caminho relativo — o arquivo some do catálogo, do scan e do diagnóstico (continua no disco).
/// <c>tipos</c>: chave de série → <c>"filme"</c> força "filme avulso" (ignora o SxxExx do nome).
/// O padrão embutido (<c>MediaNomeParser.ApelidosPadrao/NomesPadrao</c>) continua valendo; o arquivo soma e
/// sobrescreve. Lido na subida e a cada mudança (olha o arquivo a cada 15 s), ou na hora por
/// <c>POST /api/admin/recarregar-nomes</c>. Arquivo inválido NUNCA derruba o app nem zera o que já
/// valia: loga e segue com os nomes atuais.
/// </summary>
public class NomesPersonalizadosService(IConfiguration config, ILogger<NomesPersonalizadosService> logger) : BackgroundService
{
    public enum Resultado { SemArquivo, Aplicado, Invalido }

    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(15);

    private readonly string _caminho = config.GetValue<string>("NomesPath") ?? "/data/nomes.json";
    private readonly object _trava = new();
    private (DateTime Mtime, long Tamanho)? _visto;   // assinatura do arquivo na última leitura (null = não existia)
    private bool _temPersonalizado;

    public string Caminho => _caminho;

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        Recarregar();
        using var relogio = new PeriodicTimer(Intervalo);
        try
        {
            while (await relogio.WaitForNextTickAsync(parar))
                if (Mudou()) Recarregar();
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>Lê o arquivo agora; devolve o que veio dele (vazio se não existe ou é inválido).</summary>
    public (Resultado Resultado, NomesArquivo Arquivo) Recarregar()
    {
        lock (_trava)
        {
            var assinatura = Assinatura();
            _visto = assinatura;

            if (assinatura is null)
            {
                if (_temPersonalizado)
                {
                    MediaNomeParser.DefinirNomesPersonalizados(null);
                    _temPersonalizado = false;
                    logger.LogInformation("nomes.json sumiu ({Caminho}) — voltando ao padrão embutido.", _caminho);
                }
                return (Resultado.SemArquivo, NomesArquivo.Vazio);
            }

            try
            {
                var arquivo = Ler(File.ReadAllText(_caminho));
                MediaNomeParser.DefinirNomesPersonalizados(arquivo);
                _temPersonalizado = true;
                logger.LogInformation("nomes.json aplicado: {Apelidos} apelido(s), {Nomes} nome(s) de exibição, {Ignorar} padrão(ões) em ignorar, {Tipos} tipo(s).",
                    arquivo.Contagem.Apelidos, arquivo.Contagem.Nomes, arquivo.Contagem.Ignorar, arquivo.Contagem.Tipos);
                return (Resultado.Aplicado, arquivo);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("nomes.json inválido ({Caminho}): {Erro} — os nomes atuais continuam valendo.", _caminho, ex.Message);
                return (Resultado.Invalido, NomesArquivo.Vazio);
            }
        }
    }

    private bool Mudou()
    {
        lock (_trava) return Assinatura() != _visto;
    }

    private (DateTime, long)? Assinatura()
    {
        try
        {
            var fi = new FileInfo(_caminho);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Valida e extrai o conteúdo. Lança <see cref="JsonException"/>/<see cref="FormatException"/> se o arquivo
    /// não for o formato esperado; chaves desconhecidas são ignoradas.</summary>
    internal static NomesArquivo Ler(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object)
            throw new FormatException("a raiz precisa ser um objeto { \"apelidos\": {...}, \"nomes\": {...}, \"ignorar\": [...], \"tipos\": {...} }");

        var tipos = LerMapa(raiz, "tipos");
        foreach (var (chave, tipo) in tipos)
            if (tipo != NomesArquivo.TipoFilme)
                throw new FormatException($"\"tipos\" › \"{chave}\": só \"{NomesArquivo.TipoFilme}\" é aceito (veio \"{tipo}\")");

        return new NomesArquivo(LerMapa(raiz, "apelidos"), LerMapa(raiz, "nomes"), LerLista(raiz, "ignorar"), tipos);
    }

    private static List<string> LerLista(JsonElement raiz, string nome)
    {
        var lista = new List<string>();
        if (!raiz.TryGetProperty(nome, out var el)) return lista;
        if (el.ValueKind != JsonValueKind.Array)
            throw new FormatException($"\"{nome}\" precisa ser uma lista de textos");
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new FormatException($"\"{nome}\" só aceita textos");
            lista.Add(item.GetString()!);
        }
        return lista;
    }

    private static Dictionary<string, string> LerMapa(JsonElement raiz, string nome)
    {
        var mapa = new Dictionary<string, string>();
        if (!raiz.TryGetProperty(nome, out var el)) return mapa;
        if (el.ValueKind != JsonValueKind.Object)
            throw new FormatException($"\"{nome}\" precisa ser um objeto de texto -> texto");
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                throw new FormatException($"\"{nome}\" › \"{p.Name}\" precisa ser texto");
            mapa[p.Name] = p.Value.GetString()!;
        }
        return mapa;
    }
}

/// <summary>O que o <c>nomes.json</c> traz. Vazio = nada personalizado (só o padrão embutido).</summary>
public sealed record NomesArquivo(
    Dictionary<string, string> Apelidos, Dictionary<string, string> Nomes, List<string> Ignorar, Dictionary<string, string> Tipos)
{
    public const string TipoFilme = "filme";
    public static NomesArquivo Vazio => new([], [], [], []);

    /// <summary>Quantas entradas de cada tipo vieram do arquivo (pro log e pro <c>POST /api/admin/recarregar-nomes</c>).</summary>
    public ContagemNomes Contagem => new(Apelidos.Count, Nomes.Count, Ignorar.Count, Tipos.Count);
}

public record ContagemNomes(int Apelidos, int Nomes, int Ignorar, int Tipos);
