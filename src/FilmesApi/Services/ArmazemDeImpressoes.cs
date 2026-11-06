using FilmesApi.Data;
using FilmesApi.Models;

namespace FilmesApi.Services;

/// <summary>Impressões de áudio guardadas no banco (<see cref="ImpressaoSalva"/>): a reanálise usa o que já está guardado e só chama o ffmpeg
/// pro que falta — segundos em vez de reler o disco inteiro (~15 KB por janela).</summary>
public static class ArmazemDeImpressoes
{
    public const string JanelaInicio = "inicio", JanelaFim = "fim";

    public static byte[] Codificar(int[] itens)
    {
        var bytes = new byte[itens.Length * 4];
        Buffer.BlockCopy(itens, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static int[] Decodificar(byte[] dados)
    {
        var itens = new int[dados.Length / 4];
        Buffer.BlockCopy(dados, 0, itens, 0, itens.Length * 4);
        return itens;
    }

    /// <summary>A impressão guardada (se o arquivo ainda tem o mesmo tamanho) ou gerada agora com o ffmpeg e guardada. Vazio se o ffmpeg falhou.</summary>
    public static async Task<(int[] Itens, double InicioSegundos)> ObterAsync(
        AppDbContext db, FfmpegOptions ffmpeg, int filmeId, string caminho, string janela, double inicioDaJanela,
        ImpressaoDeAudio.Trecho trecho, CancellationToken ct)
    {
        long bytes;
        try { bytes = new FileInfo(caminho).Length; } catch (IOException) { return ([], 0); }

        var guardada = await db.Impressoes.FindAsync([filmeId, janela], ct);
        if (guardada is not null && guardada.ArquivoBytes == bytes) return (Decodificar(guardada.Dados), guardada.InicioSegundos);

        var itens = await ImpressaoDeAudio.ObterAsync(ffmpeg.Ffmpeg, caminho, trecho, ct);
        if (itens is null || itens.Length == 0) return ([], 0);

        if (guardada is null) db.Impressoes.Add(guardada = new ImpressaoSalva { FilmeId = filmeId, Janela = janela });
        guardada.InicioSegundos = inicioDaJanela;
        guardada.ArquivoBytes = bytes;
        guardada.Dados = Codificar(itens);
        guardada.GeradoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);   // por episódio: se o orçamento da rodada acabar, o que já foi gerado não se perde
        return (itens, inicioDaJanela);
    }
}
