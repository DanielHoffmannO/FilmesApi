using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Nome sugerido ao navegador no <c>Content-Disposition</c> de <c>/download</c>: o que a pessoa
/// vê ao salvar o arquivo. Função pura.</summary>
public static class NomeDeDownload
{
    private static readonly char[] Invalidos = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Filme: <c>Título (ano).ext</c>. Episódio: <c>Série - S05E01.ext</c> (+ <c> - Título do episódio</c>).
    /// A extensão é a do arquivo real. Sem nada aproveitável, cai pro nome real do arquivo.</summary>
    public static string Montar(FilmeResponse filme, string caminho)
    {
        var ext = Path.GetExtension(caminho);
        var nome = filme.EhEpisodio
            ? string.Join(" - ", new[] { filme.Serie, filme.Rotulo, filme.TituloEpisodio }
                .Select(Limpar).Where(p => p.Length > 0))
            : Limpar(filme.Titulo) is { Length: > 0 } t
                ? filme.AnoLancamento is int ano ? $"{t} ({ano})" : t
                : "";
        return nome.Length == 0 ? Path.GetFileName(caminho) : nome + ext;
    }

    private static string Limpar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var partes = s.Split(Invalidos).SelectMany(p => p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.Join(' ', partes);
    }
}
