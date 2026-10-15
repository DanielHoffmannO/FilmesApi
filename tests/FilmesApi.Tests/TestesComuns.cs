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

    /// <summary>Cria um executável falso (<c>#!/bin/sh</c> + <paramref name="corpo"/>) em <paramref name="pasta"/> — pra
    /// testar ffprobe/ffmpeg sem depender dos binários de verdade — e devolve o caminho.</summary>
    internal static string ScriptFalso(string pasta, string nome, string corpo)
    {
        var caminho = Path.Combine(pasta, nome);
        File.WriteAllText(caminho, "#!/bin/sh\n" + corpo + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return caminho;
    }
}
