using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

public class NomeDeDownloadTests
{
    private static FilmeResponse Filme(string titulo, int? ano = null) =>
        new(1, titulo, ano, "x", false, DateTime.UtcNow, null, null);

    private static FilmeResponse Ep(string serie, string rotulo, string? titulo = null) =>
        new(1, "t", null, "x", false, DateTime.UtcNow, null, null,
            EhEpisodio: true, Serie: serie, Rotulo: rotulo, TituloEpisodio: titulo);

    [Fact] public void Filme_com_ano() =>
        Assert.Equal("Matrix (1999).mkv", NomeDeDownload.Montar(Filme("Matrix", 1999), "/m/a.b.mkv"));

    [Fact] public void Filme_sem_ano() =>
        Assert.Equal("Matrix.mp4", NomeDeDownload.Montar(Filme("Matrix"), "/m/x.mp4"));

    [Fact] public void Episodio_sem_titulo() =>
        Assert.Equal("O Mentalista - S05E01.mkv", NomeDeDownload.Montar(Ep("O Mentalista", "S05E01"), "/m/e.mkv"));

    [Fact] public void Episodio_com_titulo() =>
        Assert.Equal("Além da Imaginação - S01E02 - O Apagador.mkv",
            NomeDeDownload.Montar(Ep("Além da Imaginação", "S01E02", "O Apagador"), "/m/e.mkv"));

    [Fact] public void Invalidos_somem_e_espacos_colapsam() =>
        Assert.Equal("Tron Legacy (2010).mkv",
            NomeDeDownload.Montar(Filme("Tron:  Legacy?", 2010), "/m/a.mkv"));

    [Fact] public void Titulo_so_de_invalidos_cai_pro_nome_do_arquivo() =>
        Assert.Equal("real.name.mkv", NomeDeDownload.Montar(Filme("???"), "/pasta/real.name.mkv"));
}

public class LimitadorDeDownloadsTests
{
    [Fact] public void Respeita_o_limite_e_libera_ao_terminar()
    {
        var l = new LimitadorDeDownloads(2);
        var a = l.TentarEntrar(); var b = l.TentarEntrar();
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.Null(l.TentarEntrar());
        a!.Dispose();
        Assert.NotNull(l.TentarEntrar());
    }

    [Fact] public void Soltar_duas_vezes_nao_libera_duas_vagas()
    {
        var l = new LimitadorDeDownloads(1);
        var a = l.TentarEntrar()!;
        a.Dispose(); a.Dispose();
        Assert.NotNull(l.TentarEntrar());
        Assert.Null(l.TentarEntrar());
    }

    [Fact] public void Zero_e_sem_limite()
    {
        var l = new LimitadorDeDownloads(0);
        for (var i = 0; i < 50; i++) Assert.NotNull(l.TentarEntrar());
    }

    [Fact] public void Padrao_e_2_e_a_config_vale()
    {
        Assert.Null(Cheio(LimitadorDeDownloads.Da(new ConfigurationBuilder().Build()), 2));
        var tres = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DownloadsSimultaneos"] = "3" }).Build();
        Assert.Null(Cheio(LimitadorDeDownloads.Da(tres), 3));
    }

    private static IDisposable? Cheio(LimitadorDeDownloads l, int n)
    {
        for (var i = 0; i < n; i++) Assert.NotNull(l.TentarEntrar());
        return l.TentarEntrar();
    }

    [Fact] public async Task Stream_solta_a_vaga_ao_terminar_e_ao_abortar()
    {
        var arq = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(arq, new byte[1000]);
            var l = new LimitadorDeDownloads(1);

            await using (var s = new StreamDeDownload(arq, l.TentarEntrar()!, NullLogger.Instance, 1, default))
            {
                Assert.Null(l.TentarEntrar());
                await s.CopyToAsync(Stream.Null);
            }
            Assert.Equal(0, l.EmAndamento);

            var cts = new CancellationTokenSource();
            var parcial = new StreamDeDownload(arq, l.TentarEntrar()!, NullLogger.Instance, 1, cts.Token);
            Assert.True(await parcial.ReadAsync(new byte[10]) > 0);
            cts.Cancel();          // cliente foi embora no meio
            parcial.Dispose();     // o ASP.NET descarta o stream
            Assert.Equal(0, l.EmAndamento);
            Assert.NotNull(l.TentarEntrar());
        }
        finally { File.Delete(arq); }
    }
}
