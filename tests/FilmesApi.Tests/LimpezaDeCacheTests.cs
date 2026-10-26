using FilmesApi.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary>O cache de HLS/remux guardava GBs de episódio já assistido: só o teto (LRU) limpava, e só quando enchia.
/// Aqui: o caminho do <c>SalvarAsync</c> que marca assistido sem descartar (causa 1), o pré-aquecimento que ignorava o
/// assistido (causa 2), e a varredura periódica (causa 3) com seu inventário pro /api/status.</summary>
public class LimpezaDeCacheTests : IDisposable
{
    private static readonly DateTime Agora = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly OpcoesLimpeza Op = new(Carencia: TimeSpan.FromMinutes(120), HlsValidade: TimeSpan.FromDays(14));

    private readonly string _raiz = TestesComuns.CriarPastaTemp();
    private readonly string _hls;
    private readonly string _remux;
    private readonly List<ScanFixture> _fixtures = [];

    public LimpezaDeCacheTests()
    {
        _hls = Path.Combine(_raiz, "hls");
        _remux = Path.Combine(_raiz, "remux");
        Directory.CreateDirectory(_hls);
        Directory.CreateDirectory(_remux);
    }

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_raiz, recursive: true);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────

    private string Hls(int id, TimeSpan parado, int bytes = 1000)
    {
        var dir = Path.Combine(_hls, id.ToString());
        Directory.CreateDirectory(dir);
        var playlist = Path.Combine(dir, "playlist.m3u8");
        File.WriteAllText(playlist, "#EXTM3U");
        File.WriteAllBytes(Path.Combine(dir, "seg_00000.ts"), new byte[bytes]);
        File.SetLastWriteTimeUtc(playlist, Agora - parado);
        return dir;
    }

    private string Remux(int id, TimeSpan parado, int bytes = 2000, string sufixo = ".mp4")
    {
        var arquivo = Path.Combine(_remux, id + sufixo);
        File.WriteAllBytes(arquivo, new byte[bytes]);
        File.SetLastWriteTimeUtc(arquivo, Agora - parado);
        return arquivo;
    }

    private static readonly TimeSpan Velho = TimeSpan.FromDays(30);
    private static readonly TimeSpan Recente = TimeSpan.FromMinutes(30);   // dentro da carência de 120 min

    private ResumoLimpeza Varrer(Dictionary<int, bool> assistidos, Func<int, TipoCache, bool>? temJob = null,
        Func<int, DateTime?>? ultimoAcesso = null) =>
        LimpezaDeCache.Varrer(_hls, _remux, assistidos, Agora, Op, new GanchosDeCache(
            ultimoAcesso ?? (_ => null), temJob ?? ((_, _) => false),
            (id, tipo) =>
            {
                if (tipo == TipoCache.Hls) Directory.Delete(Path.Combine(_hls, id.ToString()), recursive: true);
                else File.Delete(Path.Combine(_remux, id + ".mp4"));
            }));

    private bool TemHls(int id) => Directory.Exists(Path.Combine(_hls, id.ToString()));
    private bool TemRemux(int id) => File.Exists(Path.Combine(_remux, id + ".mp4"));

    // ─── varredura ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_assistido_parado_nos_dois_caches()
    {
        Hls(1, Velho); Remux(1, Velho);
        var r = Varrer(new() { [1] = true });
        Assert.False(TemHls(1));
        Assert.False(TemRemux(1));
        Assert.Equal(2, r.Itens);
        Assert.Equal(1000 + 7 + 2000, r.Bytes);   // seg (1000) + playlist ("#EXTM3U" = 7) + mp4
    }

    [Fact]
    public void Mantem_assistido_tocado_ha_pouco_quem_esta_revendo()
    {
        Hls(1, Recente); Remux(1, Recente);
        Assert.Equal(0, Varrer(new() { [1] = true }).Itens);
        Assert.True(TemHls(1) && TemRemux(1));
    }

    [Fact]
    public void Keepalive_recente_protege_mesmo_com_arquivo_velho()
    {
        Hls(1, Velho); Remux(1, Velho);
        Assert.Equal(0, Varrer(new() { [1] = true }, ultimoAcesso: _ => Agora - TimeSpan.FromMinutes(1)).Itens);
        Assert.True(TemHls(1) && TemRemux(1));
    }

    [Fact]
    public void Mantem_remux_de_nao_assistido_por_mais_velho_que_seja()
    {
        Remux(2, TimeSpan.FromDays(365));
        Assert.Equal(0, Varrer(new() { [2] = false }).Itens);
        Assert.True(TemRemux(2));
    }

    [Fact]
    public void Remove_hls_de_nao_assistido_so_depois_da_validade()
    {
        Hls(2, TimeSpan.FromDays(13));
        Hls(3, TimeSpan.FromDays(15));
        var r = Varrer(new() { [2] = false, [3] = false });
        Assert.True(TemHls(2));
        Assert.False(TemHls(3));
        Assert.Equal(1, r.Itens);
    }

    [Fact]
    public void Remove_orfao_mas_nao_quando_o_catalogo_esta_vazio()
    {
        Hls(9, Velho); Remux(9, Velho);
        // catálogo vazio (banco novo, scan ainda não rodou): tudo pareceria órfão — não decide
        Assert.Equal(0, Varrer([]).Itens);
        Assert.True(TemHls(9) && TemRemux(9));

        // catálogo com outro filme: o 9 é órfão de verdade
        Assert.Equal(2, Varrer(new() { [1] = false }).Itens);
        Assert.False(TemHls(9) || TemRemux(9));
    }

    [Fact]
    public void Remove_tmp_e_audio_soltos_mas_nao_o_audio_de_um_mp4_vivo()
    {
        Remux(1, Velho, sufixo: ".mp4.tmp");                      // job que morreu
        Remux(2, Velho, sufixo: ".mp4.audio");                    // anotação sem o .mp4
        Remux(3, Velho); Remux(3, Velho, sufixo: ".mp4.audio");   // par completo, não assistido
        var r = Varrer(new() { [1] = false, [2] = false, [3] = false });
        Assert.False(File.Exists(Path.Combine(_remux, "1.mp4.tmp")));
        Assert.False(File.Exists(Path.Combine(_remux, "2.mp4.audio")));
        Assert.True(TemRemux(3));
        Assert.True(File.Exists(Path.Combine(_remux, "3.mp4.audio")));
        Assert.Equal(2, r.Itens);
    }

    [Fact]
    public void Remove_pasta_hls_vazia()
    {
        Directory.CreateDirectory(Path.Combine(_hls, "5"));
        Directory.SetLastWriteTimeUtc(Path.Combine(_hls, "5"), Agora - Velho);
        Assert.Equal(1, Varrer(new() { [5] = false }).Itens);
        Assert.False(TemHls(5));
    }

    [Fact]
    public void Nao_toca_job_em_andamento_nem_o_tmp_dele()
    {
        Hls(1, Velho); Remux(1, Velho); Remux(2, Velho, sufixo: ".mp4.tmp");
        var r = Varrer(new() { [1] = true, [2] = false }, temJob: (_, _) => true);
        Assert.Equal(0, r.Itens);
        Assert.True(TemHls(1) && TemRemux(1));
        Assert.True(File.Exists(Path.Combine(_remux, "2.mp4.tmp")));
    }

    [Fact]
    public void E_idempotente_e_ignora_o_que_nao_e_do_cache()
    {
        Hls(1, Velho); Remux(1, Velho);
        File.WriteAllText(Path.Combine(_remux, "README.txt"), "meu");
        Directory.CreateDirectory(Path.Combine(_hls, "nao-e-id"));
        Assert.Equal(2, Varrer(new() { [1] = true, [2] = false }).Itens);
        Assert.Equal(0, Varrer(new() { [1] = true, [2] = false }).Itens);
        Assert.True(File.Exists(Path.Combine(_remux, "README.txt")));
        Assert.True(Directory.Exists(Path.Combine(_hls, "nao-e-id")));
    }

    [Fact]
    public void Nao_segue_link_simbolico_pra_fora_do_cache()
    {
        if (OperatingSystem.IsWindows()) return;
        var fora = Path.Combine(_raiz, "fora");
        Directory.CreateDirectory(fora);
        File.WriteAllText(Path.Combine(fora, "importante.txt"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_hls, "7"), fora);
        Varrer(new() { [1] = true });
        Assert.True(File.Exists(Path.Combine(fora, "importante.txt")));
    }

    // ─── inventário (/api/status) ────────────────────────────────────────────────────

    [Fact]
    public void Inventario_conta_cada_categoria_e_o_parado_ha_mais_de_7_dias()
    {
        Remux(1, Velho, 100);                                   // assistido, parado
        Remux(2, TimeSpan.FromHours(1), 200);                   // não assistido, recente
        Remux(3, TimeSpan.FromDays(8), 400);                    // órfão, parado
        Hls(1, TimeSpan.FromDays(2), 10);                       // assistido (7 bytes de playlist + 10)
        var inv = LimpezaDeCache.Inventariar(_hls, _remux, new Dictionary<int, bool> { [1] = true, [2] = false }, Agora, _ => null, tetoHls: 5000, tetoRemux: long.MaxValue);

        Assert.Equal(new ContagemCache(1, 100), inv.Remux.Assistido);
        Assert.Equal(new ContagemCache(1, 200), inv.Remux.NaoAssistido);
        Assert.Equal(new ContagemCache(1, 400), inv.Remux.Orfao);
        Assert.Equal(new ContagemCache(2, 500), inv.Remux.ParadoMais7Dias);
        Assert.Equal(0, inv.Remux.TetoBytes);       // sem teto
        Assert.Equal(new ContagemCache(1, 17), inv.Hls.Assistido);
        Assert.Equal(new ContagemCache(0, 0), inv.Hls.ParadoMais7Dias);
        Assert.Equal(5000, inv.Hls.TetoBytes);
    }

    // ─── ponta a ponta com banco e DI de verdade ─────────────────────────────────────

    private static async Task<int> Importar(ScanFixture f, string relativo)
    {
        f.Arquivo(relativo);
        await f.ScanAsync();
        return await f.IdDeAsync(relativo);
    }

    [Fact]
    public async Task Salvar_progresso_perto_do_fim_marca_assistido_e_descarta_o_cache()
    {
        var f = new ScanFixture();
        _fixtures.Add(f);
        var id = await Importar(f, "Series/Dark/Dark.S01E01.mkv");
        var (dirHls, remux) = f.CriarCache(id);
        File.WriteAllText(remux + ".audio", "1");

        // créditos: sai do player sem disparar 'ended' nem /concluir — só o salvarProgresso final
        await f.NoEscopoAsync(async sp => await sp.GetRequiredService<ProgressoService>().SalvarAsync(id, 3595, 3600));

        Assert.False(Directory.Exists(dirHls));
        Assert.False(File.Exists(remux));
        Assert.False(File.Exists(remux + ".audio"));
    }

    [Fact]
    public async Task Salvar_progresso_no_meio_nao_mexe_no_cache()
    {
        var f = new ScanFixture();
        _fixtures.Add(f);
        var id = await Importar(f, "Series/Dark/Dark.S01E01.mkv");
        var (dirHls, remux) = f.CriarCache(id);

        await f.NoEscopoAsync(async sp => await sp.GetRequiredService<ProgressoService>().SalvarAsync(id, 1200, 3600));

        Assert.True(Directory.Exists(dirHls));
        Assert.True(File.Exists(remux));
    }

    [Fact]
    public async Task Varredura_do_servico_apaga_o_assistido_e_o_orfao_e_o_inventario_enxerga()
    {
        var f = new ScanFixture();
        _fixtures.Add(f);
        var visto = await Importar(f, "Series/Dark/Dark.S01E01.mkv");
        var novo = await Importar(f, "Series/Dark/Dark.S01E02.mkv");
        await f.MarcarAssistidoComProgressoAsync("Series/Dark/Dark.S01E01.mkv");   // marca no banco sem passar pelo descarte

        var (hlsVisto, remuxVisto) = f.CriarCache(visto);
        var (hlsNovo, remuxNovo) = f.CriarCache(novo);
        var (hlsOrfao, remuxOrfao) = f.CriarCache(999);
        foreach (var p in new[] { hlsVisto, hlsNovo, hlsOrfao })
            File.SetLastWriteTimeUtc(Path.Combine(p, "playlist.m3u8"), DateTime.UtcNow - Velho);
        foreach (var p in new[] { remuxVisto, remuxNovo, remuxOrfao })
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow - Velho);

        var servico = f.Servico<LimpezaCacheService>();
        var antes = await servico.InventarioAsync();
        Assert.Equal(1, antes.Remux.Assistido.Itens);
        Assert.Equal(1, antes.Remux.NaoAssistido.Itens);
        Assert.Equal(1, antes.Remux.Orfao.Itens);
        Assert.Equal(3, antes.Hls.ParadoMais7Dias.Itens);

        var r = await servico.ExecutarUmaVezAsync();

        Assert.Equal(5, r.Itens);   // assistido (hls+remux) + órfão (hls+remux) + hls do não assistido velho
        Assert.False(Directory.Exists(hlsVisto) || File.Exists(remuxVisto));
        Assert.False(Directory.Exists(hlsOrfao) || File.Exists(remuxOrfao));
        Assert.True(File.Exists(remuxNovo));        // remux de não assistido fica
        Assert.False(Directory.Exists(hlsNovo));    // HLS de não assistido parado há 30 dias (> 14) sai
        Assert.Equal(0, (await servico.ExecutarUmaVezAsync()).Itens);   // idempotente
    }

    // ─── pré-aquecimento ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pre_aquecimento_de_episodio_assistido_nao_inicia_job_e_o_de_nao_assistido_inicia()
    {
        var ffprobe = TestesComuns.ScriptFalso(_raiz, "ffprobe.sh", """
            echo '{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720},{"index":1,"codec_type":"audio","codec_name":"ac3","channels":2,"tags":{"language":"eng"}}],"format":{"duration":"3600.0"}}'
            """);
        var ffmpeg = TestesComuns.ScriptFalso(_raiz, "ffmpeg.sh", """
            for a; do ultimo="$a"; done
            head -c 2048 /dev/zero > "$ultimo"
            """);
        var f = new ScanFixture(ffmpeg: ffmpeg, ffprobe: ffprobe);
        _fixtures.Add(f);
        var visto = await Importar(f, "Series/Dark/Dark.S01E01.mkv");
        var novo = await Importar(f, "Series/Dark/Dark.S01E02.mkv");
        var transcode = f.Servico<HlsTranscodeService>();

        Assert.False(await transcode.PreAquecerSeNecessarioAsync(visto, f.Arquivo("Series/Dark/Dark.S01E01.mkv"), jaAssistido: true, CancellationToken.None));
        Assert.True(await transcode.PreAquecerSeNecessarioAsync(novo, f.Arquivo("Series/Dark/Dark.S01E02.mkv"), jaAssistido: false, CancellationToken.None));
    }
}
