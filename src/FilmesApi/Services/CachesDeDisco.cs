namespace FilmesApi.Services;

/// <summary>Onde ficam os caches em disco. <c>CachePath</c> (padrão <c>/data</c>) é a raiz: HLS em
/// <c>{raiz}/hls</c>, remux em <c>{raiz}/remux</c>, legendas em <c>{raiz}/subs</c>. Cada um ainda pode ser
/// apontado separado (<c>HlsCachePath</c>, <c>RemuxCachePath</c>, <c>SubtitleCachePath</c>) e ganha da raiz.
/// Serve pra tirar a escrita pesada do NVMe do sistema e mandar pro SSD de mídia.</summary>
internal static class CachesDeDisco
{
    public static string Hls(IConfiguration config) => Resolver(config, "HlsCachePath", "hls");
    public static string Remux(IConfiguration config) => Resolver(config, "RemuxCachePath", "remux");
    public static string Legendas(IConfiguration config) => Resolver(config, "SubtitleCachePath", "subs");

    /// <summary>Quem foi acessado há menos que isso pode estar em uso: a expulsão por teto não toca.</summary>
    public static readonly TimeSpan MargemDeUsoNaExpulsao = TimeSpan.FromMinutes(30);

    /// <summary>Estourou o teto: expulsa até sobrar esta fração dele (folga pra não expulsar de novo no próximo job).</summary>
    public const double FracaoAlvoAoExpulsar = 0.9;

    /// <summary>Quanto tempo um retrato do tamanho do cache serve (a página de status faz poll a cada ~3 s e varrer o disco é caro).</summary>
    public static readonly TimeSpan ValidadeDoRetrato = TimeSpan.FromSeconds(20);

    private static string Resolver(IConfiguration config, string chaveEspecifica, string subpasta) =>
        config.GetValue<string>(chaveEspecifica) is { Length: > 0 } especifico
            ? especifico
            : Path.Combine(config.GetValue<string>("CachePath") is { Length: > 0 } raiz ? raiz : "/data", subpasta);
}

/// <summary>Quanto o servidor escreveu nos caches de HLS/remux desde que subiu (inclusive o que já foi
/// apagado: tentativa abortada, reencode de fallback, episódio descartado depois de assistido) — pra ver se a
/// gravação em disco está saindo do controle. Contagem aproximada, pelo tamanho dos arquivos na hora em que o
/// job termina ou é jogado fora.</summary>
internal sealed class EscritaDeCache
{
    private long _bytes;

    public DateTime Desde { get; } = DateTime.UtcNow;
    public long Bytes => Interlocked.Read(ref _bytes);

    public void ContarPasta(string pasta) => Somar(TamanhoDaPasta(pasta));

    public void ContarArquivo(string arquivo)
    {
        try { if (File.Exists(arquivo)) Somar(new FileInfo(arquivo).Length); }
        catch (IOException) { }
    }

    private void Somar(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _bytes, bytes);
    }

    internal static long TamanhoDaPasta(string pasta)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(pasta)) return 0;
            foreach (var arquivo in Directory.EnumerateFiles(pasta))
                try { total += new FileInfo(arquivo).Length; } catch (IOException) { }
        }
        catch (IOException) { }
        return total;
    }
}
