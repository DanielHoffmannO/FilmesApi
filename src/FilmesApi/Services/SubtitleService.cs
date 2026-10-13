using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>
/// Lista as faixas de legenda do vídeo — embutidas (via <see cref="MediaProbeService"/>) e
/// externas (.srt/.ass soltos ao lado do arquivo, ver <see cref="LegendaExternaLocalizador"/>)
/// — e converte as de texto pra WebVTT, servível como <c>&lt;track&gt;</c>. O HLS/stream
/// direto continua sem legenda muxada — isto é sidecar, gerado sob demanda.
///
/// Índice (<c>Idx</c>): as embutidas ocupam 0..N-1 (ordem do ffprobe) e as externas continuam
/// de N em diante, então o endpoint <c>/legenda/{idx}</c> e o <c>legendaIdx</c> do controle
/// remoto seguem sendo um inteiro só, sem o front precisar saber a origem.
///
/// Legenda bitmap (PGS/VobSub) não dá pra converter em texto — <c>Convertivel = false</c>.
/// </summary>
public class SubtitleService
{
    private static readonly HashSet<string> CodecsTexto = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "subviewer", "subviewer1",
        "microdvd", "mpl2", "pjs", "jacosub", "sami", "realtext", "stl", "vplayer",
    };

    // Legenda de texto tem poucas centenas de KB; acima disso é arquivo errado (ou lixo).
    private const long LimiteBytesLegendaExterna = 5_000_000;

    private readonly string _ffmpegPath;
    private readonly string _cachePath;
    private readonly MediaProbeService _probe;
    private readonly ILogger<SubtitleService> _logger;
    private readonly SemaphoreSlim _extracao = new(1, 1);
    // Conversão que falhou não é tentada de novo a cada pedido (cada tentativa ocupa o _extracao
    // e roda ffmpeg): espera alguns minutos antes de aceitar outra.
    private static readonly TimeSpan EsperaAposFalha = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTime> _falhas = new();
    private readonly ConcurrentDictionary<(int FilmeId, int Idx), byte> _preparando = new();
    private readonly CancellationToken _pararToken;

    public SubtitleService(FfmpegOptions ffmpeg, IConfiguration config, MediaProbeService probe,
        ILogger<SubtitleService> logger, IHostApplicationLifetime lifetime)
    {
        _ffmpegPath = ffmpeg.Ffmpeg;
        _probe = probe;
        _pararToken = lifetime.ApplicationStopping;
        // Cache próprio, longe do churn do HLS (LimparDir a cada fallback de encode, poda por
        // teto, limpeza pós-restart) — senão o .vtt some e é re-extraído toda hora.
        _cachePath = config.GetValue<string>("SubtitleCachePath") ?? "/data/subs";
        _logger = logger;
        try { Directory.CreateDirectory(_cachePath); } catch (IOException) { }
    }

    /// <summary>Apaga o .vtt cacheado do filme (chamado ao deletar o filme do catálogo).</summary>
    public void LimparCache(int filmeId)
    {
        try
        {
            var dir = Path.Combine(_cachePath, filmeId.ToString());
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Faixas de legenda do arquivo (embutidas, depois externas). <c>Idx</c> é o
    /// índice usado no endpoint <c>/legenda/{idx}</c>; nas embutidas é o relativo (0,1,2…)
    /// de <c>-map 0:s:N</c>.</summary>
    public async Task<List<LegendaInfo>> ListarAsync(string arquivoAbsoluto, CancellationToken ct) =>
        OrdenarPorRelevancia((await ListarCompletoAsync(arquivoAbsoluto, ct)).Todas);

    /// <summary>Quão boa é a faixa pra quem quer legenda em português (menor = melhor): 0
    /// português completa, 1 português SDH/CC, 2 idioma não informado (comum em .srt solto),
    /// 3 outro idioma, 4 forçada (só traduz fala estrangeira pontual), 5 bitmap (não converte).
    /// "Forçada" também vale pelo título: arquivo com a flag errada ("FORÇADA PT-BR" sem
    /// <c>forced</c>) é comum em release.</summary>
    internal static int Relevancia(LegendaInfo f)
    {
        if (!f.Convertivel) return 5;
        var tokens = LegendaExternaLocalizador.Normalizar(f.Titulo ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Forced || tokens.Any(t => t.StartsWith("forc", StringComparison.Ordinal))) return 4;
        if (EscolhaAudio.EhPortugues(f.Idioma))
            return tokens.Any(t => t is "sdh" or "cc" or "hearing") ? 1 : 0;
        return string.IsNullOrEmpty(f.Idioma) || f.Idioma == "und" ? 2 : 3;
    }

    /// <summary>Melhor faixa primeiro; o resto na ordem original (sort estável). O <c>Idx</c> de
    /// cada faixa não muda — é a identidade dela em <c>/legenda/{idx}</c> — só a posição na lista,
    /// que é o que o botão CC do celular e o vermelho da TV usam pra escolher "a primeira".</summary>
    internal static List<LegendaInfo> OrdenarPorRelevancia(List<LegendaInfo> faixas) =>
        faixas.OrderBy(Relevancia).ToList();

    /// <summary>Qual legenda vale deixar pronta no cache. Só quem assiste com áudio que não é
    /// em português usa legenda: com áudio em português, ou de idioma desconhecido (não dá pra
    /// saber), devolve null. <paramref name="faixasOrdenadas"/> vem de <see cref="OrdenarPorRelevancia"/>;
    /// só serve português ou idioma não informado — legenda em inglês pra áudio em inglês não.</summary>
    internal static int? LegendaParaPreparar(string? idiomaDoAudio, IReadOnlyList<LegendaInfo> faixasOrdenadas)
    {
        // "und"/"unk": o arquivo não diz (ffprobe repete o que o muxer gravou) — não adivinha.
        if (string.IsNullOrEmpty(idiomaDoAudio) || idiomaDoAudio.ToLowerInvariant() is "und" or "unk") return null;
        if (EscolhaAudio.EhPortugues(idiomaDoAudio)) return null;
        var melhor = faixasOrdenadas.FirstOrDefault();
        return melhor is not null && Relevancia(melhor) <= 2 ? melhor.Idx : null;
    }

    /// <summary>Extrai/converte a legenda pro cache sem ninguém esperar. A 1ª extração de uma
    /// legenda embutida num MKV grande lê o arquivo inteiro e passa dos 20 s que a TV espera —
    /// feita quando o filme abre, já está pronta quando o usuário liga a legenda. Idempotente:
    /// o mesmo (filme, faixa) não roda duas vezes ao mesmo tempo, e o cache e o recuo depois de
    /// falha de <see cref="ObterVttAsync"/> evitam repetir trabalho.</summary>
    public void PrepararEmSegundoPlano(int filmeId, string arquivoAbsoluto, int idx)
    {
        if (!_preparando.TryAdd((filmeId, idx), 0)) return;
        _ = Task.Run(async () =>
        {
            try { await ObterVttAsync(filmeId, arquivoAbsoluto, idx, _pararToken); }
            catch (OperationCanceledException) { }   // servidor parando
            catch (Exception ex) { _logger.LogWarning(ex, "Preparo da legenda {Idx} do filme {Id} falhou.", idx, filmeId); }
            finally { _preparando.TryRemove((filmeId, idx), out _); }
        });
    }

    private async Task<(List<LegendaInfo> Todas, int Embutidas, IReadOnlyList<LegendaExterna> Externas)>
        ListarCompletoAsync(string arquivoAbsoluto, CancellationToken ct)
    {
        var info = await _probe.InspecionarAsync(arquivoAbsoluto, ct);
        var todas = (info?.Legendas ?? [])
            .Select(l => new LegendaInfo(l.IdxRelativo, l.Codec, l.Idioma, l.Titulo, l.Forced, l.Default,
                Convertivel: CodecsTexto.Contains(l.Codec)))
            .ToList();
        var embutidas = todas.Count;

        var externas = LegendaExternaLocalizador.Localizar(arquivoAbsoluto);
        for (var i = 0; i < externas.Count; i++)
        {
            var e = externas[i];
            todas.Add(new LegendaInfo(embutidas + i, Path.GetExtension(e.Caminho).TrimStart('.').ToLowerInvariant(),
                e.Idioma, e.Titulo, e.Forced, Default: false, Convertivel: true));
        }
        return (todas, embutidas, externas);
    }

    /// <summary>Caminho do .vtt (extraindo/cacheando se preciso), ou null se o índice não
    /// existe, não é texto, ou a extração falhou.</summary>
    public async Task<string?> ObterVttAsync(int filmeId, string arquivoAbsoluto, int idxRelativo, CancellationToken ct)
    {
        if (idxRelativo < 0) return null;

        var dir = Path.Combine(_cachePath, filmeId.ToString());
        var destino = Path.Combine(dir, $"sub_{idxRelativo}.vtt");
        if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

        var (todas, embutidas, externas) = await ListarCompletoAsync(arquivoAbsoluto, ct);
        var faixa = todas.FirstOrDefault(x => x.Idx == idxRelativo);
        if (faixa is null || !faixa.Convertivel)
        {
            _logger.LogInformation("Legenda {Idx} do filme {Id} não é texto ({Codec}) — não dá pra servir como .vtt.",
                idxRelativo, filmeId, faixa?.Codec ?? "?");
            return null;
        }

        if (idxRelativo >= embutidas)
            return await ObterVttExternoAsync(filmeId, externas[idxRelativo - embutidas], dir, ct);

        if (FalhouRecentemente(destino)) return null;
        await _extracao.WaitAsync(ct);
        string? parcial = null;
        try
        {
            if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

            Directory.CreateDirectory(dir);
            // Grava num .tmp e só move no fim: se o processo morrer no meio (shutdown), o
            // cache nunca vê um .vtt pela metade com Length > 0.
            parcial = destino + ".tmp";
            var psi = new ProcessStartInfo(_ffmpegPath);
            foreach (var arg in new[]
            {
                "-y", "-i", arquivoAbsoluto,
                "-map", $"0:s:{idxRelativo}", "-c:s", "webvtt", "-f", "webvtt",
                parcial,
            })
                psi.ArgumentList.Add(arg);

            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(3), null, _pararToken);
            if (exit != 0 || !File.Exists(parcial) || new FileInfo(parcial).Length == 0)
            {
                _logger.LogWarning("Extração da legenda {Idx} do filme {Id} falhou (exit {Exit}): {Err}",
                    idxRelativo, filmeId, exit, stderr);
                _falhas[destino] = DateTime.UtcNow;
                return null;
            }
            File.Move(parcial, destino, overwrite: true);
            return destino;
        }
        finally
        {
            ApagarSilencioso(parcial);
            _extracao.Release();
        }
    }

    /// <summary>Converte um .srt/.ass externo pra WebVTT. O texto é relido como UTF-8 antes do
    /// ffmpeg (que assume UTF-8): legenda brasileira costuma ser Windows-1252 e viraria
    /// "a��o". Cache por caminho+mtime+tamanho — trocar o arquivo invalida sozinho, sem
    /// depender do índice (que muda se aparecer outra legenda na pasta).</summary>
    private async Task<string?> ObterVttExternoAsync(int filmeId, LegendaExterna externa, string dir, CancellationToken ct)
    {
        FileInfo fi;
        try { fi = new FileInfo(externa.Caminho); if (!fi.Exists) return null; }
        catch (IOException) { return null; }
        if (fi.Length > LimiteBytesLegendaExterna) return null;

        var chave = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{externa.Caminho}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}")))[..16];
        var destino = Path.Combine(dir, $"ext_{chave}.vtt");
        if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

        if (FalhouRecentemente(destino)) return null;
        await _extracao.WaitAsync(ct);
        string? origem = null, parcial = null;
        try
        {
            if (File.Exists(destino) && new FileInfo(destino).Length > 0) return destino;

            Directory.CreateDirectory(dir);
            var texto = LegendaExternaLocalizador.DecodificarTexto(await File.ReadAllBytesAsync(externa.Caminho, ct));
            // Nomes distintos do destino: uma legenda externa que já é .vtt não pode ser
            // entrada e saída do ffmpeg ao mesmo tempo.
            origem = Path.Combine(dir, $"ext_{chave}.src{Path.GetExtension(externa.Caminho).ToLowerInvariant()}");
            parcial = destino + ".tmp";
            await File.WriteAllTextAsync(origem, texto, new UTF8Encoding(false), ct);

            var psi = new ProcessStartInfo(_ffmpegPath);
            foreach (var arg in new[] { "-y", "-i", origem, "-f", "webvtt", parcial })
                psi.ArgumentList.Add(arg);

            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, TimeSpan.FromMinutes(1), null, _pararToken);
            if (exit != 0 || !File.Exists(parcial) || new FileInfo(parcial).Length == 0)
            {
                _logger.LogWarning("Conversão da legenda externa {Arquivo} do filme {Id} falhou (exit {Exit}): {Err}",
                    externa.Caminho, filmeId, exit, stderr);
                _falhas[destino] = DateTime.UtcNow;
                return null;
            }
            File.Move(parcial, destino, overwrite: true);
            return destino;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Não consegui ler a legenda externa {Arquivo} do filme {Id}.", externa.Caminho, filmeId);
            return null;
        }
        finally
        {
            ApagarSilencioso(origem);
            ApagarSilencioso(parcial);
            _extracao.Release();
        }
    }

    private bool FalhouRecentemente(string destino) =>
        _falhas.TryGetValue(destino, out var quando) && DateTime.UtcNow - quando < EsperaAposFalha;

    private static void ApagarSilencioso(string? arquivo)
    {
        try { if (arquivo is not null && File.Exists(arquivo)) File.Delete(arquivo); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
