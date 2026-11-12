using System.ComponentModel;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Resultado de <see cref="SaudeBibliotecaService.Disparar"/> (o <c>POST /api/diagnostico/biblioteca/verificar</c>).</summary>
public enum DisparoDeSaude { Iniciada, JaRodando, ServidorOcupado }

/// <summary>O que uma rodada fez. <c>Completa</c>: chegou ao fim do catálogo sem parar por servidor ocupado, orçamento ou falhas seguidas.</summary>
internal sealed record ResultadoRodada(bool Executou, int Analisados, int Reaproveitados, int Ausentes, bool Completa, string? Pausa, string Texto)
{
    internal static readonly ResultadoRodada JaRodando = new(false, 0, 0, 0, false, "já tem uma rodada em andamento", "já tem uma rodada em andamento");
}

/// <summary>Tudo que vai pro arquivo JSON: o cache por arquivo (chave = <see cref="SaudeBiblioteca.ChaveDeCache"/>), o último relatório e quando a última
/// rodada completa terminou.</summary>
internal sealed class ArquivoDeSaude
{
    public int Versao { get; set; } = SaudeBiblioteca.VersaoDoCache;
    public DateTime? UltimaRodadaCompleta { get; set; }
    public RelatorioSaude? Relatorio { get; set; }
    public Dictionary<string, ResultadoArquivoSaude> Entradas { get; set; } = [];
}

/// <summary>
/// "Saúde da biblioteca": em segundo plano, 1x por dia, olha cada arquivo do acervo e RELATA o que está errado — arquivo TRUNCADO (cortado no download) ou
/// ilegível, DUPLICATAS (mesmo nome em formatos diferentes; mesmo episódio em 2 arquivos), arquivo SEM PORTUGUÊS e vídeo que exige TRANSCODE PESADO. Nunca
/// apaga, move nem altera arquivo de mídia. Regras em <see cref="SaudeBiblioteca"/>.
/// <para>Prioridade baixa: um arquivo por vez, pausa entre eles, NADA roda com transcode/remux em andamento, governador térmico segurando ou placa quente, e
/// cancela com o servidor. O resultado de cada arquivo fica num JSON no diretório de dados, chaveado por caminho+tamanho+mtime: só o que mudou é reanalisado.
/// Uma rodada interrompida (servidor ocupado, orçamento de tempo) continua na próxima checagem — a cada 30 min — até completar; a seguinte só 24 h depois.</para>
/// Config: <c>SaudeBibliotecaIntervaloHoras</c> (padrão 24; 0 desliga), <c>SaudeBibliotecaAtrasoInicialSegundos</c> (600), <c>SaudeBibliotecaPausaSegundos</c> (2,
/// entre arquivos), <c>SaudeBibliotecaOrcamentoMinutos</c> (20, por rodada automática; 0 = sem teto), <c>SaudeBibliotecaJanelaFimSegundos</c> (10),
/// <c>SaudeBibliotecaTimeoutSegundos</c> (60, da leitura perto do fim), <c>SaudeBibliotecaPath</c> (padrão <c>saude-biblioteca.json</c> ao lado do <c>NomesPath</c>).
/// </summary>
public class SaudeBibliotecaService(IServiceScopeFactory escopos, FfmpegOptions ffmpeg, MediaProbeService probe, HlsTranscodeService transcode,
    ThermalService thermal, IConfiguration config, ILogger<SaudeBibliotecaService> logger, RegistroDeTarefas? tarefas = null) : BackgroundService
{
    /// <summary>Nome da tarefa em <c>/api/status</c>.</summary>
    public const string NomeDaTarefa = "saude-biblioteca";

    /// <summary>Leituras seguidas que falham: parece montagem caída — a rodada para (e não grava essas falhas) em vez de gastar 30 s de ffprobe por arquivo.</summary>
    internal const int LimiteDeFalhasSeguidas = 5;

    private const int SalvarACada = 20;
    private static readonly TimeSpan ChecagemMaxima = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _caminho = config.GetValue<string>("SaudeBibliotecaPath") is { Length: > 0 } p
        ? p
        : Path.Combine(Path.GetDirectoryName(config.GetValue<string>("NomesPath") ?? "/data/nomes.json") is { Length: > 0 } d ? d : "/data", "saude-biblioteca.json");
    private readonly string _mediaPath = config.GetValue<string>("MediaPath") ?? "/media";
    private readonly TimeSpan _pausaEntreArquivos = TimeSpan.FromSeconds(Math.Max(0, config.GetValue<double?>("SaudeBibliotecaPausaSegundos") ?? 2));
    private readonly TimeSpan _orcamento = TimeSpan.FromMinutes(Math.Max(0, config.GetValue<double?>("SaudeBibliotecaOrcamentoMinutos") ?? 20));
    private readonly double _janelaFimSegundos = Math.Max(2, config.GetValue<double?>("SaudeBibliotecaJanelaFimSegundos") ?? 10);
    private readonly TimeSpan _timeoutLeitura = TimeSpan.FromSeconds(Math.Max(5, config.GetValue<double?>("SaudeBibliotecaTimeoutSegundos") ?? 60));
    private readonly double _tetoTermico = config.GetValue<double?>("ThermalPauseCelsius") ?? 0;

    private readonly object _trava = new();
    private ArquivoDeSaude? _arquivo;
    private volatile RelatorioSaude? _relatorio;
    private int _rodando;
    private CancellationToken _parar;
    private int _falhaAoSalvarLogada;

    private Func<string?>? _motivoDeEspera;

    /// <summary>Por que NÃO trabalhar agora (null = pode): servidor com transcode/remux ou governador térmico segurando (<see cref="HlsTranscodeService.Ocioso"/>),
    /// ou placa acima do teto <c>ThermalPauseCelsius</c>. <c>internal set</c>: os testes simulam o servidor ocupado.</summary>
    internal Func<string?> MotivoDeEspera
    {
        get => _motivoDeEspera ?? MotivoPadrao;
        set => _motivoDeEspera = value;
    }

    private string? MotivoPadrao()
    {
        if (!transcode.Ocioso) return "servidor ocupado (transcode/remux ativo)";
        if (_tetoTermico > 0 && thermal.TemperaturaC() is double t && t >= _tetoTermico) return $"placa quente ({t:0} °C)";
        return null;
    }

    public bool Rodando => Volatile.Read(ref _rodando) == 1;

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        _parar = parar;
        var horas = config.GetValue<double?>("SaudeBibliotecaIntervaloHoras") ?? 24;
        if (horas <= 0)
        {
            tarefas?.Declarar(NomeDaTarefa, ligada: false);
            logger.LogInformation("Saúde da biblioteca desligada (SaudeBibliotecaIntervaloHoras=0).");
            return;
        }
        tarefas?.Declarar(NomeDaTarefa);

        var intervalo = TimeSpan.FromHours(horas);
        try
        {
            // dá tempo ao scan e à análise de abertura (que também leem o disco) de passarem na frente
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue<double?>("SaudeBibliotecaAtrasoInicialSegundos") ?? 600), parar);
            using var relogio = new PeriodicTimer(intervalo < ChecagemMaxima ? intervalo : ChecagemMaxima);
            do
            {
                // servidor ocupado na hora marcada: nem começa (e nem conta como execução); tenta de novo na próxima checagem
                if (SaudeBiblioteca.Devida(Estado().UltimaRodadaCompleta, DateTime.UtcNow, intervalo) && MotivoDeEspera() is null)
                    await ExecutarRodadaAsync(manual: false, parar);
            } while (await relogio.WaitForNextTickAsync(parar));
        }
        catch (OperationCanceledException) { /* servidor parando */ }
    }

    /// <summary>O último relatório (o da última rodada, completa ou parcial; vazio se nunca rodou). Não toca em disco de mídia.</summary>
    public RelatorioSaude ObterRelatorio()
    {
        var estado = Estado();
        var r = _relatorio ?? estado.Relatorio ?? new RelatorioSaude(null, 0, 0, SaudeBiblioteca.Resumir([]), []);
        return r with { Rodando = Rodando, UltimaRodadaCompleta = estado.UltimaRodadaCompleta };
    }

    /// <summary>Começa uma rodada agora, em segundo plano, sem esperar a hora (o botão da página): já rodando ou servidor ocupado não começa.</summary>
    public DisparoDeSaude Disparar()
    {
        if (!TentarReservar()) return DisparoDeSaude.JaRodando;
        if (MotivoDeEspera() is not null) { Volatile.Write(ref _rodando, 0); return DisparoDeSaude.ServidorOcupado; }

        _ = Task.Run(async () =>
        {
            try { await ExecutarRodadaAsync(manual: true, _parar, reservada: true); }
            catch (OperationCanceledException) { /* servidor parando */ }
        });
        return DisparoDeSaude.Iniciada;
    }

    private bool TentarReservar() => Interlocked.CompareExchange(ref _rodando, 1, 0) == 0;

    /// <summary>Uma rodada; só não devolve resultado se o servidor está parando (<see cref="OperationCanceledException"/>). Rodada manual não tem
    /// orçamento de tempo. Nunca lança por falha de arquivo ou disco.</summary>
    internal async Task<ResultadoRodada> ExecutarRodadaAsync(bool manual, CancellationToken ct = default, bool reservada = false)
    {
        if (!reservada && !TentarReservar()) return ResultadoRodada.JaRodando;
        var medicao = tarefas?.Iniciar(NomeDaTarefa);
        try
        {
            var r = await RodarAsync(manual, ct);
            medicao?.Ok(r.Texto);
            return r;
        }
        catch (OperationCanceledException)
        {
            medicao?.Ok("interrompida: servidor parando");
            throw;
        }
        catch (Exception ex)
        {
            medicao?.Falhou(ex);
            logger.LogWarning(ex, "Saúde da biblioteca falhou — tenta de novo na próxima checagem.");
            return new ResultadoRodada(true, 0, 0, 0, false, ex.Message, "falhou: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _rodando, 0);
        }
    }

    private async Task<ResultadoRodada> RodarAsync(bool manual, CancellationToken ct)
    {
        using var escopo = escopos.CreateScope();
        var filmes = escopo.ServiceProvider.GetRequiredService<FilmeService>();
        var catalogo = await filmes.ListarAsync();
        var estado = Estado();
        var entradas = estado.Entradas;
        var limite = manual || _orcamento <= TimeSpan.Zero ? (DateTime?)null : DateTime.UtcNow + _orcamento;

        var chavesAtuais = new Dictionary<string, string>(StringComparer.Ordinal);
        var falhas = new List<(string Chave, ResultadoArquivoSaude Resultado)>();
        int analisados = 0, reaproveitados = 0, ausentes = 0, desdeSalvar = 0;
        string? pausa = null;

        foreach (var f in catalogo)
        {
            ct.ThrowIfCancellationRequested();
            if (f.ArquivoPath is not { } relativo) continue;
            var caminho = await filmes.ResolverCaminhoAsync(f.Id);
            if (caminho is null) { ausentes++; continue; }   // sumiu do disco (ou montagem caída): o scan cuida, aqui só pula

            long bytes; DateTime mtime;
            try { var fi = new FileInfo(caminho); bytes = fi.Length; mtime = fi.LastWriteTimeUtc; }
            catch (IOException) { ausentes++; continue; }
            var chave = SaudeBiblioteca.ChaveDeCache(relativo, bytes, mtime);
            chavesAtuais[relativo] = chave;
            if (entradas.TryGetValue(chave, out var guardada) && SaudeBiblioteca.EntradaVale(guardada, DateTime.UtcNow)) { reaproveitados++; continue; }

            // daqui pra baixo é trabalho de verdade (processo, leitura de disco): só com o servidor livre e dentro do orçamento
            if (MotivoDeEspera() is { } motivo) { pausa = motivo; break; }
            if (limite is DateTime fimDoOrcamento && DateTime.UtcNow > fimDoOrcamento) { pausa = "orçamento da rodada esgotado"; break; }

            var (resultado, leu) = await AnalisarArquivoAsync(relativo, caminho, bytes, ct);
            analisados++;
            if (leu)
            {
                foreach (var (k, r) in falhas) entradas[k] = r;   // falha isolada: era mesmo um arquivo ilegível
                falhas.Clear();
                entradas[chave] = resultado;
            }
            else
            {
                falhas.Add((chave, resultado));
                if (falhas.Count >= LimiteDeFalhasSeguidas)
                {
                    falhas.Clear();   // não grava: provavelmente o disco caiu, não são 5 arquivos ruins
                    pausa = $"{LimiteDeFalhasSeguidas} leituras seguidas falharam (montagem caída?)";
                    break;
                }
            }
            if (++desdeSalvar >= SalvarACada) { desdeSalvar = 0; Salvar(estado); }
            if (_pausaEntreArquivos > TimeSpan.Zero) await Task.Delay(_pausaEntreArquivos, ct);
        }
        foreach (var (k, r) in falhas) entradas[k] = r;

        var completa = pausa is null;
        var noCatalogo = catalogo.Select(f => f.ArquivoPath).OfType<string>().ToHashSet(StringComparer.Ordinal);
        SaudeBiblioteca.PodarEntradas(entradas, noCatalogo, chavesAtuais);
        if (completa) estado.UltimaRodadaCompleta = DateTime.UtcNow;

        var relatorio = MontarRelatorio(catalogo, entradas, chavesAtuais, estado.UltimaRodadaCompleta);
        _relatorio = relatorio;
        estado.Relatorio = relatorio;
        Salvar(estado);

        var texto = $"{SaudeBiblioteca.ResumoEmTexto(relatorio.Resumo)} · {analisados} analisado(s) agora, {relatorio.Analisados} de {relatorio.Total} no acervo"
            + (ausentes > 0 ? $" · {ausentes} sem arquivo no disco" : "")
            + (pausa is null ? "" : $" · pausado: {pausa}");
        if (analisados > 0)
            logger.LogInformation("Saúde da biblioteca: {Texto}.", texto);
        return new ResultadoRodada(true, analisados, reaproveitados, ausentes, completa, pausa, texto);
    }

    /// <summary>O relatório a partir do cache: achados guardados de cada arquivo (o da chave atual; o que a rodada não chegou a olhar usa o mais recente
    /// que houver) + as duplicatas do catálogo inteiro, que não dependem de análise nenhuma.</summary>
    private RelatorioSaude MontarRelatorio(List<FilmeResponse> catalogo, Dictionary<string, ResultadoArquivoSaude> entradas,
        Dictionary<string, string> chavesAtuais, DateTime? ultimaCompleta)
    {
        var maisRecente = entradas.Values.GroupBy(e => e.Arquivo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(e => e.AnalisadoEm)!, StringComparer.Ordinal);
        ResultadoArquivoSaude? EntradaDe(string caminho) =>
            chavesAtuais.TryGetValue(caminho, out var k) && entradas.TryGetValue(k, out var e) ? e : maisRecente.GetValueOrDefault(caminho);

        var achados = SaudeBiblioteca.ItensDosAchados(catalogo, EntradaDe);
        var ruins = achados.Where(i => i.Tipo is TipoSaude.Truncado or TipoSaude.Ilegivel && i.Severidade == SeveridadeSaude.Ruim)
            .Select(i => i.FilmeId).ToHashSet();
        var duplicatas = SaudeBiblioteca.ItensDeDuplicatas(SaudeBiblioteca.DuplicatasDoCatalogo(catalogo, ruins, TamanhoNoDisco));
        var analisados = catalogo.Count(f => f.ArquivoPath is { } c && EntradaDe(c) is not null);
        return SaudeBiblioteca.MontarRelatorio(DateTime.UtcNow, catalogo.Count, analisados, achados.Concat(duplicatas), ultimaCompleta);
    }

    /// <summary>Tamanho no disco (só pros arquivos de grupos de duplicata, pra escolher o candidato). null se não deu pra ler.</summary>
    private long? TamanhoNoDisco(string relativo)
    {
        try { return new FileInfo(Path.Combine(_mediaPath, relativo)).Length; }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    // ─── um arquivo ──────────────────────────────────────────────────────────────────

    /// <summary>ffprobe → cabeçalho do container → leitura perto do fim → português → compatibilidade. <c>Leu</c> false = o ffprobe não leu o arquivo.</summary>
    private async Task<(ResultadoArquivoSaude Resultado, bool Leu)> AnalisarArquivoAsync(string relativo, string caminho, long bytes, CancellationToken ct)
    {
        MediaInfo? info = null;
        try { info = await probe.InspecionarAsync(caminho, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* ffprobe ausente/quebrado: conta como ilegível */ }

        var achados = new List<AchadoSaude>();
        if (info is null)
        {
            var ilegivel = SaudeBiblioteca.DecidirTruncado(new DadosDeTruncamento(false, bytes, null, false, default, null));
            achados.Add(new AchadoSaude(TipoSaude.Ilegivel, SeveridadeSaude.Ruim, ilegivel.Detalhe!));
            return (new ResultadoArquivoSaude(relativo, DateTime.UtcNow, false, achados), false);
        }

        var temVideo = info.VideoCodec is not null;
        var estrutura = LerEstrutura(caminho, bytes);
        LeituraDoFim? fim = null;
        // com o cabeçalho já dizendo "cortado" a leitura perto do fim não acrescenta nada (e num mkv sem índice seria uma leitura enorme à toa)
        if (estrutura.Estado != EstadoEstrutura.Truncada && info.DuracaoSegundos is double duracao
            && duracao >= SaudeBiblioteca.DuracaoMinimaParaLerFimSegundos && (temVideo || info.Audios.Count > 0))
            fim = await LerFimAsync(caminho, duracao, temVideo, ct);

        var decisao = SaudeBiblioteca.DecidirTruncado(new DadosDeTruncamento(true, bytes, info.DuracaoSegundos, temVideo, estrutura, fim));
        if (decisao.Veredito != VeredictoTruncamento.Ok)
            achados.Add(new AchadoSaude(TipoSaude.Truncado,
                decisao.Veredito == VeredictoTruncamento.Truncado ? SeveridadeSaude.Ruim : SeveridadeSaude.Atencao, decisao.Detalhe!));

        var idiomasExternos = LegendaExternaLocalizador.Localizar(caminho).Select(l => l.Idioma).ToList();
        if (SaudeBiblioteca.SemPortugues(info, idiomasExternos) is { } semPt)
            achados.Add(new AchadoSaude(TipoSaude.SemPortugues, SeveridadeSaude.Info, semPt));
        if (SaudeBiblioteca.TranscodePesado(info) is { } pesado)
            achados.Add(new AchadoSaude(TipoSaude.TranscodePesado, SeveridadeSaude.Info, pesado));

        return (new ResultadoArquivoSaude(relativo, DateTime.UtcNow, true, achados), true);
    }

    private static ResultadoEstrutura LerEstrutura(string caminho, long bytes)
    {
        try
        {
            using var h = File.OpenHandle(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SaudeBiblioteca.VerificarEstrutura(Path.GetExtension(caminho), bytes, (pos, n) =>
            {
                var buf = new byte[n];
                var lidos = RandomAccess.Read(h, buf, pos);
                return lidos == n ? buf : buf[..lidos];
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Lê os últimos segundos declarados com o ffmpeg (<c>-c copy</c> pro muxer nulo: não decodifica) pelo <see cref="ProcessRunner"/>, com timeout curto.
    /// Qualquer coisa que não seja uma resposta clara (timeout, ffmpeg ausente) é "inconclusiva" — nunca acusa.</summary>
    private async Task<LeituraDoFim> LerFimAsync(string caminho, double duracao, bool temVideo, CancellationToken ct)
    {
        var (inicio, janela) = SaudeBiblioteca.JanelaDoFim(duracao, _janelaFimSegundos);
        var psi = new ProcessStartInfo(ffmpeg.Ffmpeg);
        foreach (var a in SaudeBiblioteca.ArgsLeituraDoFim(caminho, inicio, janela, temVideo)) psi.ArgumentList.Add(a);
        try
        {
            var (exit, stderr) = await ProcessRunner.ExecutarComTimeoutAsync(psi, _timeoutLeitura, null, ct);
            return SaudeBiblioteca.InterpretarLeituraDoFim(exit, stderr, janela);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Saúde da biblioteca: não consegui rodar o ffmpeg em {Arquivo}.", caminho);
            return new LeituraDoFim(false, 0, null, janela);
        }
    }

    // ─── cache em disco ──────────────────────────────────────────────────────────────

    private ArquivoDeSaude Estado()
    {
        lock (_trava) return _arquivo ??= Ler();
    }

    private ArquivoDeSaude Ler()
    {
        try
        {
            if (!File.Exists(_caminho)) return new ArquivoDeSaude();
            var lido = JsonSerializer.Deserialize<ArquivoDeSaude>(File.ReadAllText(_caminho), Json);
            return lido is { Versao: SaudeBiblioteca.VersaoDoCache } ? lido : new ArquivoDeSaude();   // heurística nova: recomeça
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("saude-biblioteca.json ilegível ({Caminho}): {Erro} — recomeçando do zero.", _caminho, ex.Message);
            return new ArquivoDeSaude();
        }
    }

    /// <summary>Grava por cima de forma atômica (arquivo temporário + troca): uma queda de luz no meio não deixa JSON pela metade.</summary>
    private void Salvar(ArquivoDeSaude estado)
    {
        try
        {
            var dir = Path.GetDirectoryName(_caminho);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _caminho + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(estado, Json));
            File.Move(tmp, _caminho, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Exchange(ref _falhaAoSalvarLogada, 1) == 0)
                logger.LogWarning("Saúde da biblioteca: não consegui gravar {Caminho} ({Erro}) — o resultado fica só em memória até o próximo reinício.", _caminho, ex.Message);
        }
    }
}

public static class SaudeBibliotecaServiceCollectionExtensions
{
    /// <summary>Registra a "Saúde da biblioteca" (serviço único + rodada diária em segundo plano).</summary>
    public static IServiceCollection AddSaudeBiblioteca(this IServiceCollection servicos)
    {
        servicos.AddSingleton<SaudeBibliotecaService>();
        servicos.AddHostedService(sp => sp.GetRequiredService<SaudeBibliotecaService>());
        return servicos;
    }
}
