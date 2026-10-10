namespace FilmesApi.Tests;

/// <summary>Helpers compartilhados entre arquivos de teste — era criação de pasta temporária
/// copiada igual em mais de um lugar.</summary>
internal static class TestesComuns
{
    internal static string CriarPastaTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "filmesapi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
