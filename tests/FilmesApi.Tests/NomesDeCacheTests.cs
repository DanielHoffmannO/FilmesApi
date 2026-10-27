using FilmesApi.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary>Os nomes de arquivo dos caches estão em disco no servidor de produção: mudar qualquer um deixaria o cache existente órfão.
/// Este teste prende os valores — se precisar mudar, é decisão consciente (com compatibilidade), não efeito colateral de refactor.</summary>
public class NomesDeCacheTests
{
    [Fact]
    public void Nomes_de_arquivo_dos_caches_nao_mudam()
    {
        Assert.Equal("playlist.m3u8", NomesDeCache.Playlist);
        Assert.Equal("seg_*.ts", NomesDeCache.Segmentos);
        Assert.Equal("seg_%05d.ts", NomesDeCache.SegmentoFfmpeg);
        Assert.Equal(".mp4", NomesDeCache.ExtensaoRemux);
        Assert.Equal("*.mp4", NomesDeCache.PadraoRemux);
        Assert.Equal(".tmp", NomesDeCache.SufixoTmp);
        Assert.Equal(".audio", NomesDeCache.SufixoFaixa);
        Assert.Equal(".remux", NomesDeCache.MarcadorRemux);
    }

    [Fact]
    public async Task Caminho_do_remux_e_da_faixa_seguem_o_formato_em_producao()
    {
        using var f = new ScanFixture();
        var transcode = await f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<HlsTranscodeService>()));

        Assert.Equal(Path.Combine(f.CacheRemux, "7.mp4"), transcode.CaminhoRemux(7));
        Assert.Equal(Path.Combine(f.CacheHls, "7", "playlist.m3u8"), transcode.CaminhoPlaylist(7));
    }
}
