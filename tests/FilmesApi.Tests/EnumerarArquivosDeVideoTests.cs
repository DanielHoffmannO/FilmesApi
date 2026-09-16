using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Regressão: um symlink apontando pra um ancestral (ou pra si mesmo) — comum em backup/mount
/// mal configurado — fazia a pilha de diretórios pendentes crescer pra sempre: escaneava o
/// mesmo lugar por um caminho "novo" (via o link) infinitamente. Testa contra symlinks de
/// verdade (Linux) em vez de mockar o filesystem.
/// </summary>
public class EnumerarArquivosDeVideoTests
{
    [Fact]
    public async Task Symlink_apontando_pra_si_mesmo_nao_trava_a_varredura()
    {
        var raiz = CriarPastaTemp();
        try
        {
            File.WriteAllBytes(Path.Combine(raiz, "filme.mp4"), []);
            Directory.CreateSymbolicLink(Path.Combine(raiz, "loop"), raiz);  // aponta pra si mesma

            var tarefa = Task.Run(() => FilmeService.EnumerarArquivosDeVideo(raiz).ToList());
            var venceu = await Task.WhenAny(tarefa, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.True(venceu == tarefa, "EnumerarArquivosDeVideo não terminou em 5s — ciclo de symlink não foi cortado.");
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    [Fact]
    public async Task Symlink_apontando_pro_pai_nao_duplica_nem_trava()
    {
        var raiz = CriarPastaTemp();
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(raiz, "sub")).FullName;
            File.WriteAllBytes(Path.Combine(raiz, "filme1.mp4"), []);
            File.WriteAllBytes(Path.Combine(sub, "filme2.mkv"), []);
            Directory.CreateSymbolicLink(Path.Combine(sub, "volta"), raiz);  // sub/volta -> raiz

            var tarefa = Task.Run(() => FilmeService.EnumerarArquivosDeVideo(raiz).ToList());
            var venceu = await Task.WhenAny(tarefa, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.True(venceu == tarefa, "EnumerarArquivosDeVideo não terminou em 5s — ciclo de symlink não foi cortado.");
            // Os 2 arquivos reais aparecem uma vez só, mesmo alcançáveis 2x (direto e via symlink).
            var achados = await tarefa;
            Assert.Equal(2, achados.Count(a => a.EndsWith("filme1.mp4") || a.EndsWith("filme2.mkv")));
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    [Fact]
    public void Sem_symlink_continua_achando_tudo_normalmente()
    {
        var raiz = CriarPastaTemp();
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(raiz, "Serie", "Temporada 1")).FullName;
            File.WriteAllBytes(Path.Combine(raiz, "Filme.mp4"), []);
            File.WriteAllBytes(Path.Combine(sub, "S01E01.mkv"), []);
            File.WriteAllBytes(Path.Combine(sub, "leia-me.txt"), []);  // não é vídeo

            var achados = FilmeService.EnumerarArquivosDeVideo(raiz).ToList();

            Assert.Equal(2, achados.Count);
            Assert.Contains(achados, a => a.EndsWith("Filme.mp4"));
            Assert.Contains(achados, a => a.EndsWith("S01E01.mkv"));
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    private static string CriarPastaTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "filmesapi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
