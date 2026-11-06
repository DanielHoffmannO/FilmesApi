namespace FilmesApi.Models;

/// <summary>Onde fica a abertura de um episódio (achada por <see cref="Services.AnaliseDeAberturaService"/>). Uma linha por
/// episódio já analisado; <see cref="InicioSegundos"/>/<see cref="FimSegundos"/> nulos = analisou e não achou nada.</summary>
public class AberturaEpisodio
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }
    public double? InicioSegundos { get; set; }
    public double? FimSegundos { get; set; }
    public DateTime AnalisadoEm { get; set; } = DateTime.UtcNow;

    /// <summary>Versão do algoritmo que produziu a linha (<see cref="Services.AnaliseDeAberturaService.VersaoAlgoritmo"/>). Linha 'auto' de versão
    /// menor entra na fila de reanálise em segundo plano.</summary>
    public int Versao { get; set; } = 1;

    /// <summary>De onde veio: <c>auto</c> (comparação de áudio), <c>capitulo</c> (capítulo "Opening" do arquivo) ou <c>pulo</c> (o Daniel pulou ali).
    /// Precedência pulo &gt; capitulo &gt; auto: uma origem mais fraca nunca sobrescreve uma mais forte.</summary>
    public string Origem { get; set; } = OrigemAbertura.Auto;
}

/// <summary>Onde começam os créditos do episódio (o fim é o fim do arquivo). Mesma lógica de versão/origem de <see cref="AberturaEpisodio"/>;
/// <see cref="InicioSegundos"/> nulo = analisou e não achou.</summary>
public class CreditosEpisodio
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }
    public double? InicioSegundos { get; set; }
    public DateTime AnalisadoEm { get; set; } = DateTime.UtcNow;
    public int Versao { get; set; } = 1;
    public string Origem { get; set; } = OrigemAbertura.Auto;
}

public static class OrigemAbertura
{
    public const string Auto = "auto", Capitulo = "capitulo", Pulo = "pulo";

    private static int Forca(string origem) => origem switch { Pulo => 3, Capitulo => 2, _ => 1 };

    /// <summary>Uma marcação de origem <paramref name="nova"/> pode substituir uma de origem <paramref name="atual"/>?</summary>
    public static bool PodeSobrescrever(string atual, string nova) => Forca(nova) >= Forca(atual);
}

/// <summary>Impressão de áudio (chromaprint) guardada pra não reler o disco: <c>Janela</c> = <c>inicio</c> (0–480 s) ou <c>fim</c> (últimos 300 s).
/// <c>InicioSegundos</c> é onde a janela começa DENTRO do arquivo (0 no início; duração−300 no fim). <c>ArquivoBytes</c> invalida se o arquivo for trocado.</summary>
public class ImpressaoSalva
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }
    public string Janela { get; set; } = "";
    public double InicioSegundos { get; set; }
    public long ArquivoBytes { get; set; }
    public byte[] Dados { get; set; } = [];
    public DateTime GeradoEm { get; set; } = DateTime.UtcNow;
}
