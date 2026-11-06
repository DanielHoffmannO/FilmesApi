using System.Text;

namespace FilmesApi.Services;

/// <summary>Lê os capítulos embutidos no arquivo como fonte EXATA de abertura e de créditos: "Opening"/"Intro"/"Abertura"/"OP" e
/// "Credits"/"Ending"/"Créditos"/"Encerramento"/"ED". Pura (recebe os capítulos do ffprobe).</summary>
public static class CapituloDeAbertura
{
    public enum Tipo { Outro, Abertura, Creditos }

    /// <summary>"Opening" de menos de 5 s não é abertura (marca de cena, 2,3 s no S4E28 de Attack on Titan).</summary>
    public const double AberturaMinimaSegundos = 5;

    private static readonly HashSet<string> Aberturas = ["opening", "intro", "abertura", "op"];
    private static readonly HashSet<string> Creditos = ["credits", "ending", "creditos", "encerramento", "ed"];

    /// <summary>Título sem acento, minúsculo e sem número/pontuação sobrando ("Opening 1" → "opening").</summary>
    internal static string Normalizar(string? titulo)
    {
        var sb = new StringBuilder();
        foreach (var c in (titulo ?? "").Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetter(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static Tipo Classificar(string? titulo)
    {
        var t = Normalizar(titulo);
        return Aberturas.Contains(t) ? Tipo.Abertura : Creditos.Contains(t) ? Tipo.Creditos : Tipo.Outro;
    }

    /// <summary>A primeira abertura de pelo menos 5 s, ou null.</summary>
    public static (double Inicio, double Fim)? Abertura(IReadOnlyList<CapituloArquivo>? capitulos) =>
        capitulos?.Where(c => Classificar(c.Titulo) == Tipo.Abertura && c.FimSegundos - c.InicioSegundos >= AberturaMinimaSegundos)
            .Select(c => ((double Inicio, double Fim)?)(c.InicioSegundos, c.FimSegundos)).FirstOrDefault();

    /// <summary>Onde começam os créditos (o último capítulo "Credits"/"Ending"), ou null.</summary>
    public static double? InicioDosCreditos(IReadOnlyList<CapituloArquivo>? capitulos) =>
        capitulos?.Where(c => Classificar(c.Titulo) == Tipo.Creditos).Select(c => (double?)c.InicioSegundos).LastOrDefault();
}
