namespace FilmesApi.Models;

public class Filme
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;

    // Coluna legada: o banco (criado por uma versão anterior do modelo, com EGenero e sem
    // migrations — só Database.EnsureCreated(), que não altera tabela já existente) ainda
    // tem "Genero" como INTEGER NOT NULL sem default. O commit 75fcc34 removeu esse campo do
    // modelo sem migrar o schema, então qualquer INSERT que não o preencha quebra com
    // "SQLite Error 19: NOT NULL constraint failed: Filmes.Genero" — isso vinha silenciando
    // TODO scan/criação de filme novo (não só os afetados pelo bug do lost+found). Mantido
    // aqui só pra satisfazer a constraint; não é usado nem exposto pela API.
    public int Genero { get; set; }

    public int? AnoLancamento { get; set; }
    public string? Diretor { get; set; }

    /// <summary>Caminho relativo do arquivo de vídeo na pasta de mídia.</summary>
    public string? ArquivoPath { get; set; }

    public bool Assistido { get; set; }
    public DateTime DataAdicionado { get; set; } = DateTime.UtcNow;

    /// <summary>Ponto de retomada da reprodução, se houver (ver <see cref="ProgressoReproducao"/>).</summary>
    public ProgressoReproducao? Progresso { get; set; }
}
