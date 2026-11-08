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
    public async Task Terminar_o_episodio_NAO_apaga_o_cache_na_hora()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Show.S01E01.mkv");
        f.Arquivo("Series/Show.S01E02.mkv");
        await f.ScanAsync();
        var (id1, id2) = (await f.IdDeAsync("Series/Show.S01E01.mkv"), await f.IdDeAsync("Series/Show.S01E02.mkv"));
        var c1 = f.CriarCache(id1);
        var c2 = f.CriarCache(id2);

        Assert.True(await f.NoEscopoAsync(sp => sp.GetRequiredService<ProgressoService>().ConcluirAsync(id1)));

        Assert.True(Directory.Exists(c1.DirHls));   // fica: pausa/seek/retomada nos créditos e rever logo depois ainda acham o arquivo
        Assert.True(File.Exists(c1.Remux));
        Assert.True(Directory.Exists(c2.DirHls));
        Assert.True(File.Exists(c2.Remux));
    }

    [Fact]
    public async Task Marcar_ou_desmarcar_como_assistido_nao_mexe_no_cache()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Show.S01E01.mkv");
        await f.ScanAsync();
        var id = await f.IdDeAsync("Series/Show.S01E01.mkv");
        var cache = f.CriarCache(id);

        await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().MarcarAssistidoAsync(id));   // → assistido
        Assert.True(Directory.Exists(cache.DirHls));
        Assert.True(File.Exists(cache.Remux));

        await f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().MarcarAssistidoAsync(id));   // → desmarcou
        Assert.True(Directory.Exists(cache.DirHls));
        Assert.True(File.Exists(cache.Remux));
    }

    [Fact]
    public async Task Assistido_so_sai_na_varredura_depois_da_carencia_contada_desde_o_ultimo_acesso()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Show.S01E01.mkv");
        await f.ScanAsync();
        var id = await f.IdDeAsync("Series/Show.S01E01.mkv");
        var cache = f.CriarCache(id);
        await f.NoEscopoAsync(sp => sp.GetRequiredService<ProgressoService>().ConcluirAsync(id));
        var limpeza = f.Servico<LimpezaCacheService>();

        File.SetLastWriteTimeUtc(cache.Remux, DateTime.UtcNow.AddMinutes(-10));   // terminou, mas o último acesso foi há 10 min
        File.SetLastWriteTimeUtc(Path.Combine(cache.DirHls, "playlist.m3u8"), DateTime.UtcNow.AddMinutes(-10));
        await limpeza.ExecutarUmaVezAsync();
        Assert.True(File.Exists(cache.Remux));   // dentro da carência (120 min): quem volta 10 min depois ainda acha o arquivo

        File.SetLastWriteTimeUtc(cache.Remux, DateTime.UtcNow.AddMinutes(-130));
        File.SetLastWriteTimeUtc(Path.Combine(cache.DirHls, "playlist.m3u8"), DateTime.UtcNow.AddMinutes(-130));
        await limpeza.ExecutarUmaVezAsync();
        Assert.False(File.Exists(cache.Remux));
        Assert.False(Directory.Exists(cache.DirHls));
    }

    [Fact]
    public async Task Cache_de_remux_acima_do_teto_despeja_o_assistido_antes_do_nao_assistido()
    {
        using var f = new ScanFixture();
        var pasta = f.CacheRemux;
        Directory.CreateDirectory(pasta);
        // 1 = não assistido (o MAIS antigo), 2 = assistido (mais novo), 3 = assistido acessado há 5 min (protegido), 4 = não assistido
        string Mp4(int id, TimeSpan idade)
        {
            var c = Path.Combine(pasta, id + ".mp4");
            File.WriteAllBytes(c, new byte[1000]);
            File.SetLastWriteTimeUtc(c, DateTime.UtcNow - idade);
            return c;
        }
        var (a1, a2, a3, a4) = (Mp4(1, TimeSpan.FromHours(9)), Mp4(2, TimeSpan.FromHours(3)), Mp4(3, TimeSpan.FromMinutes(5)), Mp4(4, TimeSpan.FromHours(6)));
        var assistidos = new HashSet<int> { 2, 3 };

        // total 4000 > teto 2500; alvo 2250 → precisa apagar 2: o assistido (2) e, só então, o não assistido mais antigo (1)
        var despejados = HlsTranscodeService.DespejarExcedenteRemux(pasta, 2500, DateTime.UtcNow, assistidos.Contains);

        Assert.Equal(["2.mp4", "1.mp4"], despejados.Select(x => x.Nome));
        Assert.True(File.Exists(a3));   // assistido mas usado há 5 min: protegido pela margem de 30 min
        Assert.True(File.Exists(a4));
        Assert.False(File.Exists(a1));
        Assert.False(File.Exists(a2));
    }

    [Fact]
    public async Task Cache_de_hls_acima_do_teto_despeja_o_assistido_antes_do_nao_assistido_e_respeita_job_e_margem()
    {
        using var f = new ScanFixture(configExtra: new() { ["HlsCacheMaxGB"] = (2500.0 / 1073741824.0).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var transcode = f.Servico<HlsTranscodeService>();
        void Hls(int id, TimeSpan idade)
        {
            var dir = Path.Combine(f.CacheHls, id.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "seg_0.ts"), new byte[1000]);
            var pl = Path.Combine(dir, "playlist.m3u8");
            File.WriteAllText(pl, "#EXTM3U");
            File.SetLastWriteTimeUtc(pl, DateTime.UtcNow - idade);
        }
        Hls(1, TimeSpan.FromHours(9)); Hls(2, TimeSpan.FromHours(3)); Hls(3, TimeSpan.FromMinutes(5)); Hls(4, TimeSpan.FromHours(6));
        transcode.ObterAssistidos = () => new HashSet<int> { 2, 3 };

        transcode.LimparCacheExcedente();

        Assert.False(Directory.Exists(Path.Combine(f.CacheHls, "2")));   // assistido sai primeiro
        Assert.False(Directory.Exists(Path.Combine(f.CacheHls, "1")));   // depois o não assistido mais antigo
        Assert.True(Directory.Exists(Path.Combine(f.CacheHls, "3")));    // assistido mas dentro da margem de 30 min
        Assert.True(Directory.Exists(Path.Combine(f.CacheHls, "4")));
        await Task.CompletedTask;
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
