namespace FilmesApi.Services;

internal enum TipoCache { Hls, Remux }

internal enum CategoriaCache { Assistido, NaoAssistido, Orfao }

/// <summary>Uma entrada do cache em disco: a pasta HLS de um filme (<c>hls/{id}/</c>) ou o <c>remux/{id}.mp4</c>.</summary>
internal sealed record EntradaCache(int Id, TipoCache Tipo, string Caminho, long Bytes, DateTime Acesso);

/// <summary>Lixo que não é uma entrada: <c>{id}.mp4.tmp</c> de job que não existe mais, <c>{id}.mp4.audio</c> sem o .mp4,
/// pasta HLS vazia.</summary>
internal sealed record SobraCache(string Caminho, int Id, TipoCache Tipo, long Bytes, DateTime Acesso, bool EhPasta);

/// <summary>O que a varredura precisa saber do <see cref="HlsTranscodeService"/> sem depender dele (e sem I/O de verdade nos testes).</summary>
internal sealed record GanchosDeCache(
    Func<int, DateTime?> UltimoAcesso, Func<int, TipoCache, bool> TemJob, Action<int, TipoCache> Apagar);

internal sealed record OpcoesLimpeza(TimeSpan Carencia, TimeSpan HlsValidade)
{
    /// <summary>Nada com acesso mais recente que isto é apagado, seja qual for a regra (alguém pode estar tocando).</summary>
    public static readonly TimeSpan MargemDeUso = TimeSpan.FromMinutes(5);
}

public record ResumoLimpeza(int Itens, long Bytes);
public record ContagemCache(int Itens, long Bytes);
public record CategoriasCache(ContagemCache Assistido, ContagemCache NaoAssistido, ContagemCache Orfao, ContagemCache ParadoMais7Dias, long TetoBytes);
public record InventarioCache(CategoriasCache Remux, CategoriasCache Hls);

/// <summary>
/// Quem sai do cache de HLS/remux sem esperar o teto (LRU) estourar. O cache é a maior fonte de escrita no disco e
/// guardava GBs de episódio já assistido. Regras (<see cref="Apagar"/>): assistido parado além da carência; órfão (o
/// filme não está mais no catálogo); só no HLS, não assistido parado há <c>HlsValidadeDias</c> — o remux de não assistido
/// NÃO expira por idade, o dono prepara séries inteiras de propósito. Sobras (.tmp/.audio soltos, pasta vazia) saem
/// quando ninguém as usa. Nunca apaga o que tem job ou foi usado há pouco, e só mexe em filhos diretos das duas pastas.
/// </summary>
internal static class LimpezaDeCache
{
    internal static CategoriaCache Classificar(int id, IReadOnlyDictionary<int, bool> assistidos) =>
        !assistidos.TryGetValue(id, out var visto) ? CategoriaCache.Orfao
        : visto ? CategoriaCache.Assistido : CategoriaCache.NaoAssistido;

    internal static bool Apagar(EntradaCache e, CategoriaCache categoria, bool catalogoVazio, DateTime agora, OpcoesLimpeza op)
    {
        if (e.Acesso > agora - OpcoesLimpeza.MargemDeUso) return false;
        return categoria switch
        {
            // catálogo vazio = recém-criado ou algo errado: tudo pareceria órfão, então não decide por ele
            CategoriaCache.Orfao => !catalogoVazio,
            CategoriaCache.Assistido => e.Acesso < agora - op.Carencia,
            _ => e.Tipo == TipoCache.Hls && e.Acesso < agora - op.HlsValidade,
        };
    }

    internal static (List<EntradaCache> Entradas, List<SobraCache> Sobras) Listar(string hlsDir, string remuxDir, Func<int, DateTime?> ultimoAcesso)
    {
        var entradas = new List<EntradaCache>();
        var sobras = new List<SobraCache>();
        ListarHls(hlsDir, ultimoAcesso, entradas, sobras);
        ListarRemux(remuxDir, ultimoAcesso, entradas, sobras);
        return (entradas, sobras);
    }

    private static DateTime ComAcessoRecente(DateTime mtime, int id, Func<int, DateTime?> ultimoAcesso) =>
        ultimoAcesso(id) is { } ka && ka > mtime ? ka : mtime;

    private static void ListarHls(string pasta, Func<int, DateTime?> ultimoAcesso, List<EntradaCache> entradas, List<SobraCache> sobras)
    {
        if (!Directory.Exists(pasta)) return;
        foreach (var dir in Directory.EnumerateDirectories(pasta))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.LinkTarget is not null || !int.TryParse(info.Name, out var id)) continue;
                var playlist = Path.Combine(dir, NomesDeCache.Playlist);
                var mtime = File.Exists(playlist) ? File.GetLastWriteTimeUtc(playlist) : info.LastWriteTimeUtc;
                var acesso = ComAcessoRecente(mtime, id, ultimoAcesso);
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) sobras.Add(new SobraCache(dir, id, TipoCache.Hls, 0, acesso, EhPasta: true));
                else entradas.Add(new EntradaCache(id, TipoCache.Hls, dir, EscritaDeCache.TamanhoDaPasta(dir), acesso));
            }
            catch (IOException) { /* sumiu durante a listagem */ }
        }
    }

    private static void ListarRemux(string pasta, Func<int, DateTime?> ultimoAcesso, List<EntradaCache> entradas, List<SobraCache> sobras)
    {
        if (!Directory.Exists(pasta)) return;
        foreach (var arquivo in Directory.EnumerateFiles(pasta))
        {
            try
            {
                var fi = new FileInfo(arquivo);
                if (fi.LinkTarget is not null) continue;
                var nome = fi.Name;
                var id = IdDoNome(nome);
                if (id is null) continue;
                var acesso = ComAcessoRecente(fi.LastWriteTimeUtc, id.Value, ultimoAcesso);
                if (nome.EndsWith(NomesDeCache.ExtensaoRemux, StringComparison.Ordinal))
                    entradas.Add(new EntradaCache(id.Value, TipoCache.Remux, arquivo, fi.Length, acesso));
                else if (nome.EndsWith(NomesDeCache.ExtensaoRemux + NomesDeCache.SufixoTmp, StringComparison.Ordinal)
                    || (nome.EndsWith(NomesDeCache.ExtensaoRemux + NomesDeCache.SufixoFaixa, StringComparison.Ordinal) && !File.Exists(arquivo[..^NomesDeCache.SufixoFaixa.Length])))
                    sobras.Add(new SobraCache(arquivo, id.Value, TipoCache.Remux, fi.Length, acesso, EhPasta: false));
            }
            catch (IOException) { /* sumiu durante a listagem */ }
        }
    }

    /// <summary>"123.mp4", "123.mp4.tmp", "123.mp4.audio" → 123; qualquer outro nome → null (não é nosso, não mexe).</summary>
    private static int? IdDoNome(string nome)
    {
        var ponto = nome.IndexOf('.');
        return ponto > 0 && int.TryParse(nome[..ponto], out var id) ? id : null;
    }

    /// <summary>Uma passada de limpeza. Idempotente: rodar de novo logo em seguida não acha mais nada.</summary>
    internal static ResumoLimpeza Varrer(string hlsDir, string remuxDir, IReadOnlyDictionary<int, bool> assistidos,
        DateTime agora, OpcoesLimpeza op, GanchosDeCache cache)
    {
        var (entradas, sobras) = Listar(hlsDir, remuxDir, cache.UltimoAcesso);
        var itens = 0;
        long bytes = 0;

        foreach (var e in entradas)
        {
            if (!Apagar(e, Classificar(e.Id, assistidos), assistidos.Count == 0, agora, op) || cache.TemJob(e.Id, e.Tipo)) continue;
            cache.Apagar(e.Id, e.Tipo);
            itens++;
            bytes += e.Bytes;
        }

        foreach (var s in sobras)
        {
            if (s.Acesso > agora - OpcoesLimpeza.MargemDeUso || cache.TemJob(s.Id, s.Tipo)) continue;
            if (!ApagarSobra(s)) continue;
            itens++;
            bytes += s.Bytes;
        }
        return new ResumoLimpeza(itens, bytes);
    }

    private static bool ApagarSobra(SobraCache s)
    {
        try
        {
            if (s.EhPasta) Directory.Delete(s.Caminho, recursive: false);
            else File.Delete(s.Caminho);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Retrato do que o cache guarda, por categoria — pro <c>/api/status</c>.</summary>
    internal static InventarioCache Inventariar(string hlsDir, string remuxDir, IReadOnlyDictionary<int, bool> assistidos,
        DateTime agora, Func<int, DateTime?> ultimoAcesso, long tetoHls, long tetoRemux)
    {
        var (entradas, _) = Listar(hlsDir, remuxDir, ultimoAcesso);
        return new InventarioCache(
            Resumir(entradas.Where(e => e.Tipo == TipoCache.Remux), assistidos, agora, tetoRemux),
            Resumir(entradas.Where(e => e.Tipo == TipoCache.Hls), assistidos, agora, tetoHls));
    }

    private static CategoriasCache Resumir(IEnumerable<EntradaCache> entradas, IReadOnlyDictionary<int, bool> assistidos, DateTime agora, long teto)
    {
        var lista = entradas.ToList();
        ContagemCache Contar(Func<EntradaCache, bool> filtro) =>
            new(lista.Count(filtro), lista.Where(filtro).Sum(e => e.Bytes));
        var corte = agora - TimeSpan.FromDays(7);
        return new CategoriasCache(
            Contar(e => Classificar(e.Id, assistidos) == CategoriaCache.Assistido),
            Contar(e => Classificar(e.Id, assistidos) == CategoriaCache.NaoAssistido),
            Contar(e => Classificar(e.Id, assistidos) == CategoriaCache.Orfao),
            Contar(e => e.Acesso < corte),
            teto == long.MaxValue ? 0 : teto);
    }
}
