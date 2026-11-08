namespace FilmesApi.Services;

public partial class HlsTranscodeService
{
    /// <summary>Se o cache de /remux passou do teto (<c>RemuxCacheMaxGB</c>), despeja os .mp4
    /// menos recentemente acessados até voltar pra ~90%. Sem isso, todo filme com áudio
    /// incompatível que alguém já abriu fica em cache pra sempre (cada um é o filme inteiro,
    /// ~mesmo tamanho do original).</summary>
    private void LimparRemuxCacheExcedente()
    {
        if (_remuxCacheMaxBytes == long.MaxValue) return;
        try
        {
            foreach (var (nome, bytes) in DespejarExcedenteRemux(_remuxCachePath, _remuxCacheMaxBytes, DateTime.UtcNow, EhAssistido()))
                _logger.LogInformation("Cache de remux acima do teto: despejando {Nome} ({Mb} MB).", nome, bytes / 1024 / 1024);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao avaliar/despejar cache de remux excedente.");
        }
    }

    /// <summary>De onde o despejo por teto sabe o que é assistido (o serviço de cache não tem banco). Ligado no Program.cs; sem ele, ninguém é assistido.</summary>
    internal Func<IReadOnlySet<int>> ObterAssistidos { get; set; } = () => new HashSet<int>();

    private Func<int, bool> EhAssistido()
    {
        IReadOnlySet<int> conjunto;
        try { conjunto = ObterAssistidos(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Não consegui ler os assistidos; o despejo segue só por LRU."); conjunto = new HashSet<int>(); }
        return conjunto.Contains;
    }

    /// <summary>Bytes de remux de episódios já assistidos e fora da margem de uso: o que o despejo por teto expulsaria primeiro.</summary>
    internal long BytesDeRemuxAssistidoDespejavel()
    {
        try
        {
            if (!Directory.Exists(_remuxCachePath)) return 0;
            var visto = EhAssistido();
            var corte = DateTime.UtcNow - CachesDeDisco.MargemDeUsoNaExpulsao;
            long total = 0;
            foreach (var caminho in Directory.EnumerateFiles(_remuxCachePath, NomesDeCache.PadraoRemux))
            {
                var fi = new FileInfo(caminho);
                var ponto = fi.Name.IndexOf('.');
                if (ponto > 0 && int.TryParse(fi.Name[..ponto], out var id) && visto(id) && fi.LastWriteTimeUtc < corte) total += fi.Length;
            }
            return total;
        }
        catch (IOException) { return 0; }
    }

    /// <summary>Despeja os .mp4 mais antigos de <paramref name="pasta"/> até o total cair a 90%
    /// do teto. Pula o que foi acessado há menos de 30 min (pode estar tocando). Devolve o que
    /// apagou. O tamanho é lido UMA vez, antes de apagar: <c>FileInfo.Delete()</c> invalida o
    /// cache do próprio FileInfo e ler <c>.Length</c> depois lança FileNotFoundException — era o
    /// que abortava a limpeza (com warning no log) logo na 1ª remoção de cada rodada.</summary>
    internal static List<(string Nome, long Bytes)> DespejarExcedenteRemux(string pasta, long tetoBytes, DateTime agoraUtc, Func<int, bool>? assistido = null)
    {
        var despejados = new List<(string Nome, long Bytes)>();
        if (!Directory.Exists(pasta)) return despejados;

        var arquivos = new List<(string Caminho, string Nome, long Bytes, DateTime Mtime)>();
        foreach (var caminho in Directory.EnumerateFiles(pasta, NomesDeCache.PadraoRemux))
        {
            try
            {
                var fi = new FileInfo(caminho);
                arquivos.Add((caminho, fi.Name, fi.Length, fi.LastWriteTimeUtc));
            }
            catch (IOException) { }   // sumiu entre listar e medir (outro job apagou/renomeou)
        }
        // assistidos primeiro (mais antigos antes), depois o resto por LRU
        bool Visto(string nome) => assistido is not null && int.TryParse(nome[..Math.Max(0, nome.IndexOf('.'))], out var id) && assistido(id);
        arquivos = arquivos.OrderByDescending(a => Visto(a.Nome)).ThenBy(a => a.Mtime).ToList();

        var total = arquivos.Sum(a => a.Bytes);
        if (total <= tetoBytes) return despejados;

        var alvo = (long)(tetoBytes * CachesDeDisco.FracaoAlvoAoExpulsar);
        var corte = agoraUtc - CachesDeDisco.MargemDeUsoNaExpulsao;
        foreach (var a in arquivos)
        {
            if (total <= alvo) break;
            if (a.Mtime >= corte) continue;  // acessado há pouco, pode estar em uso
            try { File.Delete(a.Caminho); File.Delete(CaminhoFaixaDoRemux(a.Caminho)); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            total -= a.Bytes;
            despejados.Add((a.Nome, a.Bytes));
        }
        return despejados;
    }

    /// <summary>Apaga best-effort o cache HLS do filme — usado ao deletar o filme do catálogo.</summary>
    public void LimparCache(int filmeId)
    {
        _ultimoAcesso.TryRemove(filmeId, out _);
        _falhas.TryRemove(filmeId, out _);
        _completos.TryRemove(filmeId, out _);
        try
        {
            lock (_decisaoLock)
            {
                var dir = DiretorioCache(filmeId);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { /* best-effort: não bloqueia a exclusão do filme */ }
        catch (UnauthorizedAccessException) { /* best-effort: não bloqueia a exclusão do filme */ }
    }

    internal string PastaHls => _cachePath;
    internal string PastaRemux => _remuxCachePath;
    internal long TetoHlsBytes => _cacheMaxBytes;
    internal long TetoRemuxBytes => _remuxCacheMaxBytes;

    /// <summary>Como a <see cref="LimpezaDeCache"/> enxerga este serviço: último interesse, job em andamento e como apagar.</summary>
    internal GanchosDeCache GanchosDeCache() => new(
        id => _ultimoAcesso.TryGetValue(id, out var visto) ? visto : null,
        (id, tipo) => tipo == TipoCache.Hls ? _jobs.ContainsKey(id) : _remuxJobs.ContainsKey(id),
        (id, tipo) => { if (tipo == TipoCache.Hls) LimparCache(id); else ApagarRemux(id); });

    private void ApagarRemux(int filmeId)
    {
        try
        {
            File.Delete(CaminhoRemux(filmeId));
            File.Delete(CaminhoFaixaDoRemux(CaminhoRemux(filmeId)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>Cancela o job de HLS/remux em andamento do filme (se houver) e apaga o cache pronto dos dois. Só para a troca de
    /// dublagem, onde regerar é obrigatório. Terminar/marcar um episódio como assistido NÃO chama isto: o cache dele fica até a
    /// carência da <see cref="LimpezaCacheService"/> passar (contada desde o último acesso) ou até o despejo por teto, que tira os
    /// assistidos primeiro. Apagar na hora quebrava pausa/seek/retomada nos créditos e refazia o remux de quem abria o episódio e saía.</summary>
    public void DescartarCaches(int filmeId)
    {
        if (_jobCancelHls.TryGetValue(filmeId, out var ctsHls))
            try { ctsHls.Cancel(); } catch (ObjectDisposedException) { }
        if (_jobCancelRemux.TryGetValue(filmeId, out var ctsRemux))
            try { ctsRemux.Cancel(); } catch (ObjectDisposedException) { }

        LimparCache(filmeId);
        try
        {
            var caminhoRemux = CaminhoRemux(filmeId);
            if (File.Exists(caminhoRemux)) File.Delete(caminhoRemux);
            File.Delete(CaminhoFaixaDoRemux(caminhoRemux));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _remuxFalhas.TryRemove(filmeId, out _);
    }

    /// <summary>Marca "assistido agora" tocando o mtime do playlist — sinal de LRU pra
    /// eviction (sobrevive a restart, ao contrário de um dicionário em memória).
    /// Throttle: só escreve se o mtime já está velho, pra não martelar disco no poll de status.</summary>
    private static void RegistrarAcesso(string playlist)
    {
        try
        {
            if (!File.Exists(playlist)) return;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(playlist) > TimeSpan.FromMinutes(2))
                File.SetLastWriteTimeUtc(playlist, DateTime.UtcNow);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Se o cache HLS total passou do teto (<c>HlsCacheMaxGB</c>), despeja diretórios até
    /// voltar pra ~90% do teto. Ordem de despejo: primeiro os assistidos, depois os remux stream-copy (pura
    /// duplicata do original), depois por acesso mais antigo (LRU). Nunca despeja um cache
    /// com job vivo nem um acessado nos últimos 30 min (alguém provavelmente assistindo).
    /// </summary>
    public void LimparCacheExcedente()
    {
        if (_cacheMaxBytes == long.MaxValue) return;

        try
        {
            if (!Directory.Exists(_cachePath)) return;

            var caches = new List<(int Id, string Dir, long Bytes, DateTime Acesso, bool Remux)>();
            long total = 0;
            foreach (var dir in Directory.EnumerateDirectories(_cachePath))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var id)) continue;
                long bytes = 0;
                DateTime acesso = Directory.GetLastWriteTimeUtc(dir);
                foreach (var arq in Directory.EnumerateFiles(dir))
                {
                    var fi = new FileInfo(arq);
                    bytes += fi.Length;
                    if (fi.Name == NomesDeCache.Playlist) acesso = fi.LastWriteTimeUtc;
                }
                // "Acessado" = mtime do playlist OU keepalive recente (_ultimoAcesso). O mtime
                // para de subir quando o playback vira só fetch de segmento, então sozinho ele
                // acha que ninguém está assistindo um filme que está tocando.
                if (_ultimoAcesso.TryGetValue(id, out var ka) && ka > acesso) acesso = ka;
                total += bytes;
                caches.Add((id, dir, bytes, acesso, File.Exists(Path.Combine(dir, NomesDeCache.MarcadorRemux))));
            }

            if (total <= _cacheMaxBytes) return;

            var alvo = (long)(_cacheMaxBytes * CachesDeDisco.FracaoAlvoAoExpulsar);
            var corte = DateTime.UtcNow - CachesDeDisco.MargemDeUsoNaExpulsao;
            var visto = EhAssistido();
            var candidatos = caches
                .Where(c => c.Acesso < corte && !_jobs.ContainsKey(c.Id))
                .OrderByDescending(c => visto(c.Id))   // assistido sai primeiro
                .ThenByDescending(c => c.Remux)      // depois remux (duplicata)
                .ThenBy(c => c.Acesso)               // depois, menos recentemente assistido
                .ToList();

            foreach (var c in candidatos)
            {
                if (total <= alvo) break;
                lock (_decisaoLock)
                {
                    if (_jobs.ContainsKey(c.Id)) continue;
                    try { Directory.Delete(c.Dir, recursive: true); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    _completos.TryRemove(c.Id, out _);
                    _falhas.TryRemove(c.Id, out _);
                }
                total -= c.Bytes;
                _logger.LogInformation(
                    "Cache HLS acima do teto: despejando filme {Id} ({Mb} MB, {Tipo}, último acesso {Acesso:u}).",
                    c.Id, c.Bytes / 1024 / 1024, c.Remux ? "remux" : "reencode", c.Acesso);
            }

            if (total > _cacheMaxBytes)
                _logger.LogWarning("Cache HLS ainda acima do teto ({Mb} MB) — todos os caches restantes " +
                    "estão em uso ou foram acessados há pouco. Nada despejado.", total / 1024 / 1024);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao avaliar/despejar cache HLS excedente.");
        }
    }
}
