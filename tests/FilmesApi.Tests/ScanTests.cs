using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>Scan de verdade contra uma pasta de mídia temporária. Os nomes de arquivo são do acervo
/// real (rip brasileiro com ruído de release), não nomes inventados.</summary>
public class ScanTests
{
    // ─── montagem caída: um disco inteiro some, o catálogo dele NÃO ─────────────────────

    [Fact]
    public async Task Disco_extra_desmontado_nao_apaga_o_catalogo_dele()
    {
        using var f = new ScanFixture();
        f.Arquivo("Filmes/Matrix.1999.1080p.BluRay.x264.mkv");
        f.Arquivo("extra/The.Sopranos.S01E01.720p.mkv");
        f.Arquivo("extra/The.Sopranos.S01E02.720p.mkv");
        Assert.Equal(3, (await f.ScanAsync()).Importados);

        // o HD de backup desmonta: /media/extra continua existindo, mas vazio
        File.Delete(Path.Combine(f.Media, "extra", "The.Sopranos.S01E01.720p.mkv"));
        File.Delete(Path.Combine(f.Media, "extra", "The.Sopranos.S01E02.720p.mkv"));

        var r = await f.ScanAsync();

        Assert.Equal(0, r.Removidos);
        Assert.Equal(3, (await f.CaminhosNoBancoAsync()).Count);
    }

    [Fact]
    public async Task Prefixo_vivo_ainda_remove_o_orfao_dele()
    {
        using var f = new ScanFixture();
        f.Arquivo("extra/As.3.Espias.Demais.S01E05.O.Apagador.mp4");
        f.Arquivo("extra/As.3.Espias.Demais.S01E06.mp4");
        await f.ScanAsync();

        File.Delete(Path.Combine(f.Media, "extra", "As.3.Espias.Demais.S01E05.O.Apagador.mp4"));   // reorganizou: sumiu SÓ esse

        var r = await f.ScanAsync();

        Assert.Equal(1, r.Removidos);
        Assert.Equal(["extra/As.3.Espias.Demais.S01E06.mp4"], await f.CaminhosNoBancoAsync());
    }

    [Fact]
    public async Task Raiz_vazia_com_outro_prefixo_vivo_mantem_os_itens_da_raiz()
    {
        using var f = new ScanFixture();
        f.Arquivo("Filme.Solto.2020.mp4");            // direto na raiz
        f.Arquivo("Series/Futurama.S02E01.A.Flight.to.Remember.1080p.DVDRip.x265-N4U.mkv");
        await f.ScanAsync();

        File.Delete(Path.Combine(f.Media, "Filme.Solto.2020.mp4"));

        var r = await f.ScanAsync();

        Assert.Equal(0, r.Removidos);   // a raiz inteira ficou sem arquivo: tratado como montagem caída
        Assert.Equal(2, (await f.CaminhosNoBancoAsync()).Count);
    }

    [Fact]
    public async Task Disco_inteiro_vazio_continua_sem_apagar_nada()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Show.S01E01.mkv");
        await f.ScanAsync();
        File.Delete(Path.Combine(f.Media, "Series", "Show.S01E01.mkv"));

        Assert.Equal(0, (await f.ScanAsync()).Removidos);
        Assert.Single(await f.CaminhosNoBancoAsync());
    }

    [Theory]
    [InlineData("extra/Serie/ep.mkv", "extra")]
    [InlineData("Filmes/x.mkv", "Filmes")]
    [InlineData("solto.mp4", "")]
    public void Prefixo_e_a_pasta_de_primeiro_nivel(string relativo, string esperado) =>
        Assert.Equal(esperado, FilmeService.PrefixoDe(relativo));

    // ─── arquivo incompleto ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("As.3.Espias.Demais.S01E05.mkv.!qB")]      // qBittorrent
    [InlineData("S07E05 - Futebol - TORRENTMEGAFILMES.TV.mkv.part")]
    [InlineData("Show.S01E01.mkv.parts")]
    public async Task Sufixo_de_download_em_andamento_nunca_importa(string nome)
    {
        using var f = new ScanFixture();
        f.Arquivo(nome);

        Assert.Equal(0, (await f.ScanAsync()).Importados);
        Assert.Empty(await f.CaminhosNoBancoAsync());
    }

    [Fact]
    public async Task Arquivo_que_ainda_cresce_fica_pro_proximo_scan()
    {
        using var f = new ScanFixture(estabilidadeSegundos: 0.4);
        var caminho = f.Arquivo("Series/Breaking.Bad.2011.S04E09.720p.BluRay.x264.mkv", "inicio");

        // alguém ainda escrevendo no arquivo durante a pausa de estabilidade do scan
        var escritor = Task.Run(async () =>
        {
            await Task.Delay(150);
            await File.AppendAllTextAsync(caminho, new string('x', 5000));
        });
        var durante = await f.ScanAsync();
        await escritor;

        Assert.Equal(0, durante.Importados);

        var depois = await f.ScanAsync();   // agora completo e parado
        Assert.Equal(1, depois.Importados);
    }

    [Fact]
    public async Task Arquivo_parado_entra_normalmente_mesmo_com_a_pausa_ligada()
    {
        using var f = new ScanFixture(estabilidadeSegundos: 0.2);
        f.Arquivo("Filmes/Matrix.1999.mkv");
        f.Arquivo("Filmes/Alien.1979.mkv");

        Assert.Equal(2, (await f.ScanAsync()).Importados);
    }

    // ─── regressões que não podem mudar ──────────────────────────────────────────────

    [Fact]
    public async Task Scan_repetido_e_idempotente()
    {
        using var f = new ScanFixture();
        f.Arquivo("Series/Show.S01E01.mkv");

        Assert.Equal(1, (await f.ScanAsync()).Importados);
        var segundo = await f.ScanAsync();

        Assert.Equal(0, segundo.Importados);
        Assert.Equal(0, segundo.Removidos);
    }
}
