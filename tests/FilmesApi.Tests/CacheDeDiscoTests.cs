using FilmesApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary>O cache de HLS/remux é a maior fonte de escrita do servidor (o NVMe do sistema já gastou 54% da vida):
/// apagar o que já foi assistido, mostrar quanto está sendo gravado e poder mandar tudo pro SSD de mídia.</summary>
public class CacheDeDiscoTests
{
    private static IConfiguration Config(params (string, string)[] pares) =>
        new ConfigurationBuilder().AddInMemoryCollection(pares.ToDictionary(p => p.Item1, p => (string?)p.Item2)).Build();

    // ─── CachePath ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Sem_nenhuma_configuracao_os_padroes_continuam_os_de_sempre()
    {
        var c = Config();
        Assert.Equal("/data/hls", CachesDeDisco.Hls(c));
        Assert.Equal("/data/remux", CachesDeDisco.Remux(c));
        Assert.Equal("/data/subs", CachesDeDisco.Legendas(c));
    }

    [Fact]
    public void CachePath_muda_a_raiz_dos_tres()
    {
        var c = Config(("CachePath", "/mnt/ssd/cache"));
        Assert.Equal("/mnt/ssd/cache/hls", CachesDeDisco.Hls(c));
        Assert.Equal("/mnt/ssd/cache/remux", CachesDeDisco.Remux(c));
        Assert.Equal("/mnt/ssd/cache/subs", CachesDeDisco.Legendas(c));
    }

    [Fact]
    public void Caminho_especifico_ganha_da_raiz()
    {
        var c = Config(("CachePath", "/mnt/ssd/cache"), ("RemuxCachePath", "/outro/remux"));
        Assert.Equal("/mnt/ssd/cache/hls", CachesDeDisco.Hls(c));
        Assert.Equal("/outro/remux", CachesDeDisco.Remux(c));
    }

    [Fact]
    public void Valor_vazio_vale_como_nao_configurado()
    {
        var c = Config(("CachePath", ""), ("HlsCachePath", ""));
        Assert.Equal("/data/hls", CachesDeDisco.Hls(c));
    }

    // ─── contagem do que foi escrito ────────────────────────────────────────────────

    [Fact]
    public void Escrita_soma_pasta_e_arquivo_e_ignora_o_que_nao_existe()
    {
        var dir = TestesComuns.CriarPastaTemp();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.ts"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "b.ts"), new byte[50]);
            var arquivo = Path.Combine(dir, "so.mp4");
            File.WriteAllBytes(arquivo, new byte[1000]);

            var escrita = new EscritaDeCache();
            escrita.ContarPasta(dir);
            escrita.ContarArquivo(arquivo);
            escrita.ContarPasta(Path.Combine(dir, "nao-existe"));
            escrita.ContarArquivo(Path.Combine(dir, "nao-existe.mp4"));

            Assert.Equal(100 + 50 + 1000 + 1000, escrita.Bytes);   // a pasta já inclui o so.mp4
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ─── apagar o cache do que já foi visto ─────────────────────────────────────────

    [Fact]
    public async Task Terminar_o_episodio_apaga_o_cache_de_hls_e_de_remux_dele_e_so_dele()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Show.S01E01.mkv");
        f.Arquivo("Series/Show.S01E02.mkv");
        await f.ScanAsync();
        var (id1, id2) = (await f.IdDeAsync("Series/Show.S01E01.mkv"), await f.IdDeAsync("Series/Show.S01E02.mkv"));
        var c1 = f.CriarCache(id1);
        var c2 = f.CriarCache(id2);

        Assert.True(await f.NoEscopoAsync(sp => sp.GetRequiredService<ProgressoService>().ConcluirAsync(id1)));

        Assert.False(Directory.Exists(c1.DirHls));
        Assert.False(File.Exists(c1.Remux));
        Assert.True(Directory.Exists(c2.DirHls));   // o outro episódio não perde nada
        Assert.True(File.Exists(c2.Remux));
    }

    [Fact]
    public async Task Marcar_como_assistido_apaga_o_cache_mas_desmarcar_nao()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Show.S01E01.mkv");
        await f.ScanAsync();
        var id = await f.IdDeAsync("Series/Show.S01E01.mkv");
        var cache = f.CriarCache(id);

        await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().MarcarAssistidoAsync(id));   // → assistido
        Assert.False(Directory.Exists(cache.DirHls));
        Assert.False(File.Exists(cache.Remux));

        var novo = f.CriarCache(id);
        await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().MarcarAssistidoAsync(id));   // → desmarcou
        Assert.True(Directory.Exists(novo.DirHls));
        Assert.True(File.Exists(novo.Remux));
    }

    [Fact]
    public async Task Concluir_sem_nenhum_cache_nao_quebra()
    {
        using var f = new ScanFixture();
        f.Arquivo("Filmes/Matrix.1999.mp4");
        await f.ScanAsync();
        var id = await f.IdDeAsync("Filmes/Matrix.1999.mp4");

        Assert.True(await f.NoEscopoAsync(sp => sp.GetRequiredService<ProgressoService>().ConcluirAsync(id)));
        Assert.False(await f.NoEscopoAsync(sp => sp.GetRequiredService<ProgressoService>().ConcluirAsync(9999)));   // filme inexistente
    }

    [Fact]
    public async Task Descartar_cache_aparece_na_conta_do_que_foi_escrito_so_quando_o_job_termina_nao_ao_apagar()
    {
        // Apagar não é escrever: o contador é de bytes GRAVADOS pelos jobs (ver HlsTranscodeService).
        using var f = new ScanFixture();
        var transcode = await f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<HlsTranscodeService>()));
        var antes = transcode.ObterSnapshot().EscritoBytes;

        var cache = f.CriarCache(3);
        transcode.DescartarCaches(3);

        Assert.Equal(antes, transcode.ObterSnapshot().EscritoBytes);
        Assert.False(Directory.Exists(cache.DirHls));
    }

    // ─── /api/status ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_mostra_o_tamanho_dos_dois_caches()
    {
        using var f = new ScanFixture();
        var transcode = await f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<HlsTranscodeService>()));
        File.WriteAllBytes(Path.Combine(f.CriarCache(1).DirHls, "seg_00001.ts"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(f.CacheRemux, "2.mp4"), new byte[7000]);

        var s = transcode.ObterSnapshot();

        Assert.Equal(1, s.CacheItens);
        Assert.True(s.CacheBytes >= 5000);
        Assert.Equal(2, s.RemuxItens);   // 1.mp4 do CriarCache + 2.mp4
        Assert.True(s.RemuxBytes >= 7000);
        Assert.True(s.EscritoDesde <= DateTime.UtcNow);
    }
}
