using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Os tipos de problema que a varredura da biblioteca relata (<c>tipo</c> de cada item e chave do <c>resumo</c>).</summary>
public static class TipoSaude
{
    public const string Truncado = "truncado";
    public const string Ilegivel = "ilegivel";
    public const string Duplicado = "duplicado";
    public const string SemPortugues = "sem_portugues";
    public const string TranscodePesado = "transcode_pesado";

    /// <summary>Ordem de exibição (e do texto do resumo): o que mais importa primeiro.</summary>
    public static readonly string[] Todos = [Truncado, Ilegivel, Duplicado, SemPortugues, TranscodePesado];
}

/// <summary><c>ruim</c>: o arquivo não toca direito; <c>atencao</c>: vale olhar; <c>info</c>: só um fato do acervo.</summary>
public static class SeveridadeSaude
{
    public const string Ruim = "ruim";
    public const string Atencao = "atencao";
    public const string Info = "info";

    internal static int Ordem(string severidade) => severidade switch { Ruim => 0, Atencao => 1, _ => 2 };
}

/// <summary>Um problema achado num arquivo (o que fica guardado no cache por arquivo).</summary>
public sealed record AchadoSaude(string Tipo, string Severidade, string Detalhe);

/// <summary>Uma linha do relatório. <c>Grupo</c>/<c>CandidatoManter</c> só existem nas duplicatas: arquivos do mesmo grupo são o mesmo
/// conteúdo, e o candidato é só uma SUGESTÃO (nada é apagado nem movido).</summary>
public sealed record ItemSaude(
    int FilmeId, string Titulo, string Arquivo, string Tipo, string Severidade, string Detalhe,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Grupo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? CandidatoManter = null);

/// <summary>O que <c>GET /api/diagnostico/biblioteca</c> devolve. <c>Resumo</c>: arquivos por tipo (duplicado conta GRUPOS, cada um com todos
/// os seus arquivos em <c>Itens</c>). <c>Analisados</c> &lt; <c>Total</c> = a primeira varredura ainda não chegou em tudo.</summary>
public sealed record RelatorioSaude(
    DateTime? GeradoEm, int Total, int Analisados, Dictionary<string, int> Resumo, List<ItemSaude> Itens,
    bool Rodando = false, DateTime? UltimaRodadaCompleta = null);

/// <summary>O que se descobriu de UM arquivo (cache em disco, chaveado por <see cref="SaudeBiblioteca.ChaveDeCache"/>). <c>Completo</c> false
/// = o ffprobe não conseguiu ler (pode ser disco acordando): vale por <see cref="SaudeBiblioteca.ReanalisarIncompletoApos"/> e depois é refeito.</summary>
internal sealed record ResultadoArquivoSaude(string Arquivo, DateTime AnalisadoEm, bool Completo, List<AchadoSaude> Achados);

internal enum EstadoEstrutura { Desconhecida, Integra, Truncada }

/// <summary>Resultado da conferência do cabeçalho do container: o arquivo tem pelo menos o tamanho que ele mesmo declara?</summary>
internal readonly record struct ResultadoEstrutura(EstadoEstrutura Estado, string Formato = "", long EsperadoBytes = 0);

/// <summary>Leitura de poucos segundos perto do fim declarado (ffmpeg com <c>-c copy</c>, sem decodificar). <c>Conclusiva</c> false = não deu pra
/// saber (timeout, ffmpeg ausente, saída irreconhecível) — nunca vira acusação.</summary>
internal sealed record LeituraDoFim(bool Conclusiva, int Frames, double? SegundosLidos, double JanelaSegundos);

internal sealed record DadosDeTruncamento(
    bool ProbeOk, long Bytes, double? DuracaoSegundos, bool TemVideo, ResultadoEstrutura Estrutura, LeituraDoFim? Fim);

internal enum VeredictoTruncamento { Ok, Truncado, Suspeito, Ilegivel }

internal sealed record DecisaoTruncamento(VeredictoTruncamento Veredito, string? Detalhe);

/// <summary>Um arquivo candidato a duplicata. <c>Ruim</c>: já apareceu como truncado/ilegível — nunca é o candidato a manter.</summary>
internal sealed record ArquivoParaDuplicata(int Id, string Titulo, string Caminho, long? Bytes, bool Ruim);

internal sealed record GrupoDuplicado(string Chave, string Motivo, List<ArquivoParaDuplicata> Arquivos);

/// <summary>
/// Regras puras da "Saúde da biblioteca" (nada de disco, processo nem banco aqui — dá pra testar tudo): decisão de arquivo TRUNCADO, DUPLICADO,
/// SEM_PORTUGUES e TRANSCODE_PESADO, chave de cache, resumo. A execução (ffprobe/ffmpeg, cache em JSON, pausa entre arquivos) está em
/// <see cref="SaudeBibliotecaService"/>. A varredura só RELATA: nunca apaga, move nem altera arquivo de mídia.
/// </summary>
internal static partial class SaudeBiblioteca
{
    /// <summary>Sobe quando a heurística muda de modo que os resultados guardados deixam de valer (o cache velho é descartado inteiro).</summary>
    internal const int VersaoDoCache = 1;

    /// <summary>Um resultado incompleto (probe falhou) é refeito depois disso — menos que o intervalo padrão de 24 h, pra a próxima rodada pegar.</summary>
    internal static readonly TimeSpan ReanalisarIncompletoApos = TimeSpan.FromHours(20);

    /// <summary>Abaixo disso (vídeo com 5+ min) a taxa de bits média é absurda: nenhum vídeo real cabe em tão pouco.</summary>
    internal const double BitrateAbsurdoKbps = 100;
    internal const double DuracaoMinimaParaBitrateSegundos = 300;

    /// <summary>Vídeo mais curto que isso não vale a leitura perto do fim (e a janela ficaria do tamanho do arquivo).</summary>
    internal const double DuracaoMinimaParaLerFimSegundos = 60;

    /// <summary>Lido menos que esta fração da janela perto do fim = os dados acabam bem antes da duração declarada.</summary>
    internal const double FracaoMinimaLidaNaJanela = 0.3;

    // ─── cache ───────────────────────────────────────────────────────────────────────

    /// <summary>Caminho (relativo à mídia) + tamanho + mtime: arquivo trocado/refeito vira chave nova e é reanalisado; o resto não.</summary>
    internal static string ChaveDeCache(string relativo, long bytes, DateTime mtimeUtc) => $"{relativo}|{bytes}|{mtimeUtc.Ticks}";

    internal static bool EntradaVale(ResultadoArquivoSaude entrada, DateTime agora) =>
        entrada.Completo || agora - entrada.AnalisadoEm < ReanalisarIncompletoApos;

    /// <summary>Tira do cache o que não serve mais: arquivo que saiu do catálogo, ou que está no catálogo com outra chave (foi trocado).
    /// Arquivo que a rodada não chegou a olhar (interrompida) ou que sumiu do disco por um instante (montagem caída) mantém a entrada.</summary>
    internal static int PodarEntradas(Dictionary<string, ResultadoArquivoSaude> entradas, IReadOnlySet<string> caminhosNoCatalogo,
        IReadOnlyDictionary<string, string> chavesAtuais)
    {
        var fora = entradas.Where(kv => !caminhosNoCatalogo.Contains(kv.Value.Arquivo)
                || (chavesAtuais.TryGetValue(kv.Value.Arquivo, out var atual) && atual != kv.Key))
            .Select(kv => kv.Key).ToList();
        foreach (var k in fora) entradas.Remove(k);
        return fora.Count;
    }

    /// <summary>Hora de uma rodada completa? Nunca rodou, ou a última completa já tem <paramref name="intervalo"/> ou mais.</summary>
    internal static bool Devida(DateTime? ultimaCompleta, DateTime agora, TimeSpan intervalo) =>
        ultimaCompleta is null || agora - ultimaCompleta.Value >= intervalo;

    // ─── truncado ────────────────────────────────────────────────────────────────────

    /// <summary>A decisão de truncado, em ordem: probe falhou = ilegível; o próprio cabeçalho declara mais bytes do que o arquivo tem = truncado
    /// (certeza); perto do fim declarado não há dados = truncado; taxa de bits média absurda pro tempo = suspeito (só "atenção").</summary>
    internal static DecisaoTruncamento DecidirTruncado(DadosDeTruncamento d)
    {
        if (!d.ProbeOk)
            return new(VeredictoTruncamento.Ilegivel, "o ffprobe não conseguiu ler o arquivo (corrompido, cortado no começo, ou disco lento/ausente)");

        if (d.Estrutura.Estado == EstadoEstrutura.Truncada)
            return new(VeredictoTruncamento.Truncado,
                $"o arquivo tem {Tamanho(d.Bytes)}, mas o próprio cabeçalho ({d.Estrutura.Formato}) declara {Tamanho(d.Estrutura.EsperadoBytes)} " +
                $"({Percentual(d.Bytes, d.Estrutura.EsperadoBytes)}%): foi cortado, provavelmente no download");

        if (d.Fim is { Conclusiva: true } fim)
        {
            var duracao = d.DuracaoSegundos is double dur ? $" da duração declarada ({Duracao(dur)})" : "";
            if (fim.Frames == 0)
                return new(VeredictoTruncamento.Truncado, $"sem dados nos últimos {fim.JanelaSegundos:0} s{duracao}: o arquivo acaba antes do fim");
            if (fim.SegundosLidos is double lido && lido < fim.JanelaSegundos * FracaoMinimaLidaNaJanela)
                return new(VeredictoTruncamento.Truncado,
                    $"só {lido.ToString("0.#", CultureInfo.InvariantCulture)} s de dados nos últimos {fim.JanelaSegundos:0} s{duracao}: o arquivo acaba antes do fim");
        }

        if (d.TemVideo && d.DuracaoSegundos is double dd && dd >= DuracaoMinimaParaBitrateSegundos)
        {
            var kbps = d.Bytes * 8.0 / dd / 1000;
            if (kbps < BitrateAbsurdoKbps)
                return new(VeredictoTruncamento.Suspeito,
                    $"taxa de bits média de {kbps:0} kbps ({Tamanho(d.Bytes)} para {Duracao(dd)}) é baixa demais para um vídeo: pode estar cortado");
        }
        return new(VeredictoTruncamento.Ok, null);
    }

    /// <summary>Confere o cabeçalho do container contra o tamanho do arquivo (sem processo, só alguns bytes lidos): Matroska/WebM declaram o tamanho do
    /// Segment, MP4/MOV a cadeia de boxes até o fim, AVI o tamanho do RIFF. <paramref name="ler"/>(posição, quantos) devolve os bytes lidos (pode ser
    /// menos). Qualquer dúvida (formato desconhecido, tamanho "desconhecido", lixo) = <see cref="EstadoEstrutura.Desconhecida"/>: nunca acusa.</summary>
    internal static ResultadoEstrutura VerificarEstrutura(string extensao, long tamanhoArquivo, Func<long, int, byte[]> ler)
    {
        try
        {
            return extensao.ToLowerInvariant() switch
            {
                ".mkv" or ".webm" => Matroska(tamanhoArquivo, ler),
                ".mp4" or ".m4v" or ".mov" => CadeiaMp4(tamanhoArquivo, ler),
                ".avi" => Avi(tamanhoArquivo, ler),
                _ => default,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return default;
        }
    }

    private static ResultadoEstrutura Matroska(long tamanho, Func<long, int, byte[]> ler)
    {
        var b = ler(0, 4096);
        if (b.Length < 16 || b[0] != 0x1A || b[1] != 0x45 || b[2] != 0xDF || b[3] != 0xA3) return default;   // sem o cabeçalho EBML
        var pos = 4;
        if (!LerVint(b, ref pos, out var tamCabecalho, out _)) return default;
        if (tamCabecalho > (ulong)(b.Length - pos)) return default;
        pos += (int)tamCabecalho;
        if (pos + 4 > b.Length || b[pos] != 0x18 || b[pos + 1] != 0x53 || b[pos + 2] != 0x80 || b[pos + 3] != 0x67) return default;   // ID do Segment
        pos += 4;
        if (!LerVint(b, ref pos, out var tamSegmento, out var desconhecido) || desconhecido) return default;   // gravação ao vivo: tamanho não declarado
        var esperado = pos + (long)tamSegmento;
        return new(tamanho < esperado ? EstadoEstrutura.Truncada : EstadoEstrutura.Integra, "Matroska", esperado);
    }

    /// <summary>Inteiro de tamanho variável do EBML (1 a 8 bytes). <paramref name="desconhecido"/>: todos os bits do valor ligados.</summary>
    private static bool LerVint(byte[] b, ref int pos, out ulong valor, out bool desconhecido)
    {
        valor = 0; desconhecido = false;
        if (pos >= b.Length || b[pos] == 0) return false;
        var tam = 1;
        while ((b[pos] & (0x80 >> (tam - 1))) == 0) tam++;
        if (pos + tam > b.Length) return false;
        valor = (ulong)(b[pos] & (0xFF >> tam));
        for (var i = 1; i < tam; i++) valor = (valor << 8) | b[pos + i];
        desconhecido = valor == (1UL << (7 * tam)) - 1;
        pos += tam;
        return true;
    }

    private static readonly HashSet<string> PrimeirasCaixas = new(StringComparer.Ordinal) { "ftyp", "moov", "mdat", "free", "skip", "wide", "pnot", "junk" };

    private static ResultadoEstrutura CadeiaMp4(long tamanho, Func<long, int, byte[]> ler)
    {
        long pos = 0;
        for (var caixas = 0; caixas < 64; caixas++)
        {
            if (pos == tamanho) return new(EstadoEstrutura.Integra, "MP4", tamanho);   // a cadeia termina exatamente no fim do arquivo
            var h = ler(pos, 16);
            if (h.Length < 8) return default;
            // a 1ª caixa de um MP4/MOV de verdade é uma destas; qualquer outra coisa (texto, outro formato com extensão errada) não é julgada
            if (caixas == 0 && !PrimeirasCaixas.Contains(System.Text.Encoding.ASCII.GetString(h, 4, 4))) return default;
            ulong tam = (ulong)h[0] << 24 | (ulong)h[1] << 16 | (ulong)h[2] << 8 | h[3];
            if (tam == 1)
            {
                if (h.Length < 16) return default;
                tam = 0;
                for (var i = 8; i < 16; i++) tam = (tam << 8) | h[i];
            }
            else if (tam == 0) return default;   // "até o fim do arquivo": não dá pra saber
            if (tam < 8 || tam > long.MaxValue / 2) return default;
            var fim = pos + (long)tam;
            if (fim > tamanho) return new(EstadoEstrutura.Truncada, "MP4", fim);
            pos = fim;
        }
        return default;
    }

    private static ResultadoEstrutura Avi(long tamanho, Func<long, int, byte[]> ler)
    {
        var b = ler(0, 12);
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'A' || b[9] != 'V' || b[10] != 'I' || b[11] != ' ') return default;
        var tam = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        if (tam < 12 || tam == uint.MaxValue) return default;
        // AVI de mais de 1 GB continua em outros blocos RIFF (OpenDML): ter MAIS que o declarado é normal; ter MENOS é corte.
        var esperado = tam + 8L;
        return new(tamanho < esperado ? EstadoEstrutura.Truncada : EstadoEstrutura.Integra, "AVI", esperado);
    }

    // ─── leitura perto do fim (ffmpeg) ───────────────────────────────────────────────

    /// <summary>Janela lida: os últimos <paramref name="janela"/> s da duração declarada. Devolve (início do seek, tamanho real da janela).</summary>
    internal static (double Inicio, double Janela) JanelaDoFim(double duracaoSegundos, double janela)
    {
        var inicio = Math.Max(0, duracaoSegundos - janela);
        return (inicio, duracaoSegundos - inicio);
    }

    /// <summary>Argumentos do ffmpeg que lê só os últimos segundos SEM decodificar (<c>-c copy</c> pro muxer nulo): barato, e o stderr diz se veio dado.
    /// <c>-ss</c> antes do <c>-i</c> = seek de entrada (usa o índice do arquivo; não decodifica o caminho até lá).</summary>
    internal static List<string> ArgsLeituraDoFim(string arquivo, double inicioSegundos, double janelaSegundos, bool temVideo) =>
    [
        "-nostdin", "-hide_banner", "-v", "info",
        "-ss", Num(inicioSegundos), "-i", arquivo,
        "-map", temVideo ? "0:v:0" : "0:a:0",
        "-t", Num(janelaSegundos * 2),   // teto de segurança: a duração declarada pode estar errada pra menos
        "-c", "copy", "-f", "null", "-",
    ];

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"frame=\s*(\d+)")] private static partial Regex ReFrames();
    [GeneratedRegex(@"time=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")] private static partial Regex ReTempo();
    [GeneratedRegex(@"(?:video|audio):(\d+)kB")] private static partial Regex ReKb();

    /// <summary>Lê o que o ffmpeg imprimiu (<c>frame=… time=…</c> e o resumo <c>video:NkB</c>). <paramref name="exitCode"/> &lt; 0 é o
    /// <see cref="ProcessRunner"/> avisando timeout/travou/não iniciou; qualquer saída que não dá pra entender também é inconclusiva.</summary>
    internal static LeituraDoFim InterpretarLeituraDoFim(int exitCode, string? stderr, double janelaSegundos)
    {
        var inconclusiva = new LeituraDoFim(false, 0, null, janelaSegundos);
        if (exitCode != 0 || string.IsNullOrEmpty(stderr)) return inconclusiva;

        int? frames = null;
        foreach (Match m in ReFrames().Matches(stderr)) frames = int.TryParse(m.Groups[1].Value, out var n) ? n : frames;
        double? tempo = null;
        foreach (Match m in ReTempo().Matches(stderr))
            tempo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600.0 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

        long? kb = null;
        foreach (Match m in ReKb().Matches(stderr)) kb = (kb ?? 0) + long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

        if (stderr.Contains("Output file is empty", StringComparison.Ordinal) || stderr.Contains("nothing was encoded", StringComparison.Ordinal))
            frames = 0;
        else if (frames is null && kb is not null)
            frames = kb > 0 ? 1 : 0;   // áudio puro não imprime frame=; o resumo diz se houve dado
        if (frames is null) return inconclusiva;
        return new LeituraDoFim(true, frames.Value, frames == 0 ? 0 : tempo, janelaSegundos);
    }

    // ─── português e transcode ───────────────────────────────────────────────────────

    private static readonly HashSet<string> TokensPortugues = new(StringComparer.Ordinal)
        { "por", "pob", "pt", "ptbr", "pt-br", "portuguese", "portugues", "brazilian", "brasil", "brasileiro" };

    /// <summary>Faixa de áudio, legenda embutida ou legenda externa em português? Faixa SEM idioma informado (o ffprobe devolve "und" ou nada, comum em
    /// rip nacional e em .srt solto) conta como "talvez português": acusar esses seria só ruído. Devolve o detalhe, ou null quando há português (ou não dá pra saber).</summary>
    internal static string? SemPortugues(MediaInfo info, IReadOnlyList<string?> idiomasDeLegendaExterna)
    {
        if (info.Audios.Count == 0) return null;
        if (info.Audios.Any(a => EscolhaAudio.EhPortugues(a.Idioma) || SemIdioma(a.Idioma))) return null;
        if (info.Legendas.Any(l => EscolhaAudio.EhPortugues(l.Idioma) || SemIdioma(l.Idioma) || TituloEhPortugues(l.Titulo))) return null;
        if (idiomasDeLegendaExterna.Any(i => EscolhaAudio.EhPortugues(i) || SemIdioma(i))) return null;

        var audios = string.Join(", ", info.Audios.Select(a => a.Idioma!.ToLowerInvariant()).Distinct());
        var legendas = info.Legendas.Count + idiomasDeLegendaExterna.Count;
        return $"áudio em {audios} e {(legendas > 0 ? $"{legendas} legenda(s) em outro idioma" : "nenhuma legenda")}: nada em português";
    }

    private static bool SemIdioma(string? idioma) => string.IsNullOrWhiteSpace(idioma) || idioma.Equals("und", StringComparison.OrdinalIgnoreCase);

    private static bool TituloEhPortugues(string? titulo) =>
        !string.IsNullOrWhiteSpace(titulo)
        && LegendaExternaLocalizador.Normalizar(titulo).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(TokensPortugues.Contains);

    /// <summary>O vídeo não toca nem copiado (<see cref="CompatibilidadeVideo.VideoTocavelSemReencode"/>: codec fora da lista curta ou 10-bit): sem
    /// compatibilidade direta nem remux (<see cref="CompatibilidadeVideo.Classificar"/> daria <c>Incompativel</c>), só reencodando o vídeo — o
    /// trabalho pesado pra esta placa. null quando toca direto, por remux, ou o arquivo não tem vídeo.</summary>
    internal static string? TranscodePesado(MediaInfo info)
    {
        if (info.VideoCodec is null || CompatibilidadeVideo.VideoTocavelSemReencode(info.VideoCodec, info.Video10Bit)) return null;
        var dez = info.Video10Bit ? " 10-bit" : "";
        var tamanho = info.Largura > 0 && info.Altura > 0 ? $" {info.Largura}x{info.Altura}" : "";
        return $"vídeo {info.VideoCodec}{dez}{tamanho}: o navegador não toca nem copiado, então reproduzir exige reencodar o vídeo (pesado nesta placa)";
    }

    // ─── duplicatas ──────────────────────────────────────────────────────────────────

    /// <summary>Quanto o formato "toca fácil" — desempata o candidato a manter: mp4/webm/mov tocam direto, mkv passa por remux, avi/wmv/flv costumam ir a HLS.</summary>
    internal static int RankDeFormato(string extensao) => extensao.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".webm" or ".mov" => 3,
        ".mkv" => 2,
        ".avi" or ".wmv" or ".flv" => 1,
        _ => 0,
    };

    /// <summary>Mesma pasta + mesmo nome-base (sem a extensão, maiúscula não conta) em 2 ou mais arquivos: <c>Barbie.avi</c> + <c>Barbie.mp4</c>.</summary>
    internal static List<GrupoDuplicado> DuplicatasPorNomeBase(IEnumerable<ArquivoParaDuplicata> arquivos) => arquivos
        .GroupBy(a => (Pasta: (Path.GetDirectoryName(a.Caminho) ?? "").Replace('\\', '/').ToLowerInvariant(),
                       Nome: Path.GetFileNameWithoutExtension(a.Caminho).ToLowerInvariant()))
        .Where(g => g.Count() >= 2)
        .OrderBy(g => g.Key.Pasta, StringComparer.Ordinal).ThenBy(g => g.Key.Nome, StringComparer.Ordinal)
        .Select(g => new GrupoDuplicado($"nome:{g.Key.Pasta}/{g.Key.Nome}",
            $"o mesmo arquivo em {g.Count()} formatos ({string.Join(", ", g.Select(a => Path.GetExtension(a.Caminho).ToLowerInvariant()).OrderBy(e => e, StringComparer.Ordinal))})",
            g.OrderBy(a => a.Caminho, StringComparer.Ordinal).ToList()))
        .ToList();

    /// <summary>O mesmo episódio (SxxEyy) em 2+ arquivos da mesma série, como o diagnóstico do catálogo já agrupa
    /// (<see cref="DiagnosticoCatalogo.Analisar"/>, parser + agrupador existentes). <paramref name="porId"/>: o catálogo por id.</summary>
    internal static List<GrupoDuplicado> DuplicatasPorEpisodio(IEnumerable<RepetidoDiag> repetidos, Func<int, ArquivoParaDuplicata?> porId) =>
        repetidos.Select(r => new GrupoDuplicado($"ep:{r.Serie.ToLowerInvariant()}|{r.Temporada}|{r.Episodio}",
                $"{r.Serie} T{r.Temporada:00}E{r.Episodio:00} em {r.Arquivos.Count} arquivos",
                r.Arquivos.Select(a => porId(a.Id)).OfType<ArquivoParaDuplicata>().OrderBy(a => a.Caminho, StringComparer.Ordinal).ToList()))
            .Where(g => g.Arquivos.Count >= 2)
            .ToList();

    /// <summary>Junta as duas regras: um grupo por episódio cujos arquivos já formam um grupo por nome-base (o mesmo par .avi/.mp4 de SxxEyy) não conta duas vezes.</summary>
    internal static List<GrupoDuplicado> JuntarDuplicatas(List<GrupoDuplicado> porNome, List<GrupoDuplicado> porEpisodio)
    {
        var resultado = new List<GrupoDuplicado>(porNome);
        foreach (var g in porEpisodio)
        {
            var ids = g.Arquivos.Select(a => a.Id).ToHashSet();
            if (!porNome.Any(n => ids.IsSubsetOf(n.Arquivos.Select(a => a.Id)))) resultado.Add(g);
        }
        return resultado;
    }

    /// <summary>As duas regras sobre o catálogo inteiro (já classificado), com o tamanho de cada arquivo de grupo (<paramref name="tamanhoDe"/>, caminho relativo → bytes)
    /// pra escolher o candidato. <paramref name="ruins"/>: ids já achados truncados/ilegíveis — nunca são o candidato.</summary>
    internal static List<GrupoDuplicado> DuplicatasDoCatalogo(IReadOnlyList<FilmeResponse> catalogo, IReadOnlySet<int> ruins, Func<string, long?> tamanhoDe)
    {
        var todos = catalogo.Where(f => f.ArquivoPath is not null)
            .Select(f => new ArquivoParaDuplicata(f.Id, TituloParaExibir(f), f.ArquivoPath!, null, ruins.Contains(f.Id))).ToList();
        var porId = todos.ToDictionary(a => a.Id);
        var grupos = JuntarDuplicatas(
            DuplicatasPorNomeBase(todos),
            DuplicatasPorEpisodio(DiagnosticoCatalogo.Analisar(catalogo, _ => null).Repetidos, id => porId.GetValueOrDefault(id)));
        foreach (var g in grupos)
            for (var i = 0; i < g.Arquivos.Count; i++) g.Arquivos[i] = g.Arquivos[i] with { Bytes = tamanhoDe(g.Arquivos[i].Caminho) };
        return grupos;
    }

    /// <summary>Índice do candidato a manter. Só uma sugestão: arquivo ruim (truncado/ilegível) nunca ganha; depois o formato que toca mais fácil; depois o maior.</summary>
    internal static int EscolherCandidato(IReadOnlyList<ArquivoParaDuplicata> arquivos)
    {
        var melhor = 0;
        for (var i = 1; i < arquivos.Count; i++)
            if (Pontos(arquivos[i]).CompareTo(Pontos(arquivos[melhor])) > 0) melhor = i;
        return melhor;
    }

    private static (int Bom, int Formato, long Bytes) Pontos(ArquivoParaDuplicata a) =>
        (a.Ruim ? 0 : 1, RankDeFormato(Path.GetExtension(a.Caminho)), a.Bytes ?? 0);

    /// <summary>Uma linha por arquivo de cada grupo, o candidato marcado — e o porquê dele no detalhe. Nada aqui apaga nem escolhe: o dono decide.</summary>
    internal static List<ItemSaude> ItensDeDuplicatas(IEnumerable<GrupoDuplicado> grupos)
    {
        var itens = new List<ItemSaude>();
        foreach (var g in grupos)
        {
            var cand = EscolherCandidato(g.Arquivos);
            var escolhido = g.Arquivos[cand];
            var outros = g.Arquivos.Where((_, i) => i != cand).ToList();
            var razao = RazaoDoCandidato(escolhido, outros);
            for (var i = 0; i < g.Arquivos.Count; i++)
            {
                var a = g.Arquivos[i];
                var detalhe = i == cand
                    ? $"{g.Motivo}. Candidato a manter: {razao}."
                    : $"{g.Motivo}. Candidato a manter: {Path.GetFileName(escolhido.Caminho)} ({razao}).";
                itens.Add(new ItemSaude(a.Id, a.Titulo, a.Caminho, TipoSaude.Duplicado, SeveridadeSaude.Atencao,
                    $"{detalhe} Tamanho deste: {(a.Bytes is long b ? Tamanho(b) : "?")}.", g.Chave, i == cand));
            }
        }
        return itens;
    }

    private static string RazaoDoCandidato(ArquivoParaDuplicata escolhido, List<ArquivoParaDuplicata> outros)
    {
        if (!escolhido.Ruim && outros.Any(o => o.Ruim)) return "os outros parecem truncados ou ilegíveis";
        var ext = Path.GetExtension(escolhido.Caminho);
        if (outros.All(o => RankDeFormato(Path.GetExtension(o.Caminho)) < RankDeFormato(ext))) return $"formato {ext.ToLowerInvariant()} toca mais fácil";
        return escolhido.Bytes is long b ? $"o maior, {Tamanho(b)}" : "o primeiro da lista";
    }

    // ─── relatório ───────────────────────────────────────────────────────────────────

    /// <summary>O nome que a pessoa reconhece: episódio vira "Série S01E05", o resto fica com o título.</summary>
    internal static string TituloParaExibir(FilmeResponse f) =>
        f.EhEpisodio && !string.IsNullOrWhiteSpace(f.Serie) ? $"{f.Serie} {f.Rotulo}".Trim() : f.Titulo;

    /// <summary>Os achados guardados de cada arquivo do catálogo viram linhas do relatório.</summary>
    internal static List<ItemSaude> ItensDosAchados(IEnumerable<FilmeResponse> catalogo, Func<string, ResultadoArquivoSaude?> entradaDe)
    {
        var itens = new List<ItemSaude>();
        foreach (var f in catalogo)
            if (f.ArquivoPath is { } caminho && entradaDe(caminho) is { } e)
                itens.AddRange(e.Achados.Select(a => new ItemSaude(f.Id, TituloParaExibir(f), caminho, a.Tipo, a.Severidade, a.Detalhe)));
        return itens;
    }

    /// <summary>Contagem por tipo na ordem fixa do contrato (truncado, duplicado, sem_portugues, transcode_pesado e, por último, ilegivel). Duplicado conta grupos.</summary>
    internal static Dictionary<string, int> Resumir(IEnumerable<ItemSaude> itens)
    {
        var lista = itens.ToList();
        int Contar(string tipo) => lista.Count(i => i.Tipo == tipo);
        return new Dictionary<string, int>
        {
            [TipoSaude.Truncado] = Contar(TipoSaude.Truncado),
            [TipoSaude.Duplicado] = lista.Where(i => i.Tipo == TipoSaude.Duplicado).Select(i => i.Grupo ?? $"#{i.FilmeId}").Distinct().Count(),
            [TipoSaude.SemPortugues] = Contar(TipoSaude.SemPortugues),
            [TipoSaude.TranscodePesado] = Contar(TipoSaude.TranscodePesado),
            [TipoSaude.Ilegivel] = Contar(TipoSaude.Ilegivel),
        };
    }

    /// <summary>"3 truncado(s), 2 duplicado(s)" — a linha da tarefa no <c>/api/status</c>. Só o que tem; nada = "nenhum problema".</summary>
    internal static string ResumoEmTexto(IReadOnlyDictionary<string, int> resumo)
    {
        var partes = new List<string>();
        foreach (var (tipo, rotulo) in new[]
                 {
                     (TipoSaude.Truncado, "truncado(s)"), (TipoSaude.Ilegivel, "ilegível(is)"), (TipoSaude.Duplicado, "duplicado(s)"),
                     (TipoSaude.SemPortugues, "sem português"), (TipoSaude.TranscodePesado, "transcode pesado"),
                 })
            if (resumo.GetValueOrDefault(tipo) is > 0 and var n) partes.Add($"{n} {rotulo}");
        return partes.Count == 0 ? "nenhum problema" : string.Join(", ", partes);
    }

    /// <summary>Ruim primeiro, depois atenção, depois info; dentro de cada, por tipo e caminho.</summary>
    internal static List<ItemSaude> Ordenar(IEnumerable<ItemSaude> itens) => itens
        .OrderBy(i => SeveridadeSaude.Ordem(i.Severidade))
        .ThenBy(i => Array.IndexOf(TipoSaude.Todos, i.Tipo))
        .ThenBy(i => i.Grupo ?? "", StringComparer.Ordinal)
        .ThenBy(i => i.Arquivo, StringComparer.Ordinal)
        .ToList();

    internal static RelatorioSaude MontarRelatorio(DateTime geradoEm, int total, int analisados, IEnumerable<ItemSaude> itens, DateTime? ultimaCompleta)
    {
        var ordenados = Ordenar(itens);
        return new RelatorioSaude(geradoEm, total, analisados, Resumir(ordenados), ordenados, false, ultimaCompleta);
    }

    // ─── texto ───────────────────────────────────────────────────────────────────────

    internal static string Tamanho(long bytes) => bytes >= 1L << 30
        ? $"{(bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',')} GB"
        : $"{Math.Max(0, bytes) / 1048576} MB";

    private static int Percentual(long tem, long esperado) => esperado <= 0 ? 0 : (int)Math.Min(100, tem * 100 / esperado);

    internal static string Duracao(double segundos)
    {
        var minutos = (int)Math.Round(segundos / 60);
        return minutos >= 60 ? $"{minutos / 60}h{minutos % 60:00}min" : $"{minutos}min";
    }
}
