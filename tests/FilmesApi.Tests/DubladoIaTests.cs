using System.Text.Json;
using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>"Dublado por IA": o <c>scripts/dublar</c> acrescenta ao MKV uma faixa AAC <c>language=por</c> com título "Português (IA)", por cima do
/// original japonês. O catálogo reconhece a faixa (título), a escolha automática prefere uma dublagem de verdade, e a lista mostra o selo sem rodar
/// ffprobe a cada requisição (flag guardado no banco por <see cref="DubladoIaService"/>).</summary>
public class DubladoIaTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    // ─── EhDubladoIa: decide só pelo título da faixa ─────────────────────────────────

    [Theory]
    [InlineData("Português (IA)", true)]
    [InlineData("português (ia)", true)]
    [InlineData("PORTUGUÊS (IA)", true)]
    [InlineData("Português (ÍA)", true)]        // acento não conta
    [InlineData("Dublagem IA)", true)]          // só o fim "IA)" já basta
    [InlineData("IA)", true)]
    [InlineData("Dublagem (IA) Kokoro", true)]
    [InlineData("Português (Itália)", false)]   // termina em "ia)" mas o IA não começa uma palavra
    [InlineData("Espanhol (Bolívia)", false)]
    [InlineData("Espanhol (Colômbia)", false)]
    [InlineData("Português (Brasil)", false)]
    [InlineData("Original", false)]
    [InlineData("Português", false)]
    [InlineData("IA", false)]                   // sem o ")" não é o marcador
    [InlineData("(IA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Titulo_marca_a_dublagem_por_ia_so_com_o_IA_entre_parenteses(string? titulo, bool esperado)
    {
        Assert.Equal(esperado, EscolhaAudio.TituloIndicaDubladoIa(titulo));
        Assert.Equal(esperado, EscolhaAudio.EhDubladoIa(new FaixaAudio(1, "aac", "por", false, 2, titulo)));
    }

    [Fact]
    public void Faixa_nula_ou_sem_titulo_nao_e_dublada_por_ia()
    {
        Assert.False(EscolhaAudio.EhDubladoIa((FaixaAudio?)null));
        Assert.False(EscolhaAudio.EhDubladoIa(new FaixaAudio(1, "aac", "por", true, 2)));   // Titulo tem default: chamadas antigas continuam valendo
    }

    // ─── Escolher: dublagem de verdade vence a sintética ─────────────────────────────

    private static FaixaAudio Jpn(int i, bool def = false) => new(i, "aac", "jpn", def, 2);
    private static FaixaAudio Por(int i, bool def = false) => new(i, "ac3", "por", def, 2);
    private static FaixaAudio PorIa(int i, bool def = false) => new(i, "aac", "por", def, 2, "Português (IA)");

    [Fact]
    public void Por_de_verdade_vence_a_ia_em_qualquer_ordem()
    {
        Assert.Equal(3, EscolhaAudio.Escolher([Jpn(1), PorIa(2, def: true), Por(3)]));
        Assert.Equal(2, EscolhaAudio.Escolher([Jpn(1), Por(2), PorIa(3, def: true)]));
        Assert.Equal(1, EscolhaAudio.Escolher([PorIa(0, def: true), Por(1)]));
    }

    [Fact]
    public void So_a_ia_em_portugues_e_escolhida_mesmo_com_o_original_como_default()
    {
        Assert.Equal(2, EscolhaAudio.Escolher([Jpn(1, def: true), PorIa(2)]));
    }

    [Fact]
    public void Duas_ia_em_portugues_ficam_com_a_primeira()
    {
        Assert.Equal(2, EscolhaAudio.Escolher([Jpn(1), PorIa(2), PorIa(3)]));
    }

    [Fact]
    public void Sem_portugues_a_regra_de_sempre_vale()
    {
        Assert.Equal(2, EscolhaAudio.Escolher([new FaixaAudio(1, "aac", "eng", false, 2), Jpn(2, def: true)]));   // a default
        Assert.Equal(1, EscolhaAudio.Escolher([Jpn(1), new FaixaAudio(2, "aac", "eng", false, 2)]));              // a primeira
    }

    [Fact]
    public void Escolha_manual_vale_mais_que_qualquer_regra_de_ia()
    {
        Assert.Equal(2, EscolhaAudio.Escolher([Jpn(1), PorIa(2), Por(3)], preferenciaManual: 2));   // o Daniel quis a IA
        Assert.Equal(1, EscolhaAudio.Escolher([Jpn(1), PorIa(2, def: true)], preferenciaManual: 1)); // o Daniel quis o original
        Assert.Equal(2, EscolhaAudio.Escolher([Jpn(1), PorIa(2)], preferenciaManual: 9));            // manual que não existe: cai na regra
    }

    [Fact]
    public void Idioma_da_serie_em_portugues_tambem_prefere_a_faixa_que_nao_e_ia()
    {
        Assert.Equal(3, EscolhaAudio.Escolher([Jpn(1), PorIa(2), Por(3)], idiomaDaSerie: "por"));
        Assert.Equal(2, EscolhaAudio.Escolher([Jpn(1), PorIa(2)], idiomaDaSerie: "pt"));   // só IA: é ela
    }

    [Fact]
    public void Serie_no_original_ignora_a_ia()
    {
        Assert.Equal(1, EscolhaAudio.Escolher([Jpn(1), PorIa(2, def: true)], idiomaDaSerie: "jpn"));
    }

    // ─── probe: a tag title chega na faixa ───────────────────────────────────────────

    [Fact]
    public void Probe_le_a_tag_title_das_faixas_de_audio()
    {
        var info = MediaProbeService.Parsear("""
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "pix_fmt": "yuv420p" },
                { "index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2,
                  "disposition": { "default": 0 }, "tags": { "language": "jpn", "title": "Japonês 2.0" } },
                { "index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2,
                  "disposition": { "default": 1 }, "tags": { "language": "por", "title": "Português (IA)" } },
                { "index": 3, "codec_type": "audio", "codec_name": "ac3", "channels": 6 },
                { "index": 4, "codec_type": "subtitle", "codec_name": "ass", "tags": { "language": "por", "title": "Português (IA)" } }
              ], "format": { "duration": "1440.0" } }
            """);

        Assert.Equal(3, info.Audios.Count);
        Assert.Equal("Japonês 2.0", info.Audios[0].Titulo);
        Assert.Equal("Português (IA)", info.Audios[1].Titulo);
        Assert.Equal("por", info.Audios[1].Idioma);
        Assert.True(info.Audios[1].Default);
        Assert.Null(info.Audios[2].Titulo);   // sem tags: título nulo, não vazio
        Assert.Equal([false, true, false], info.Audios.Select(EscolhaAudio.EhDubladoIa));
        Assert.Equal(2, EscolhaAudio.Escolher(info.Audios));   // só a IA em português: é a escolhida
    }

    // ─── DTOs ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dtos_trazem_dubladoIa_com_default_falso_e_saem_em_camelCase()
    {
        var filme = new FilmeResponse(1, "Galaxy Express 999", null, "a.mkv", false, DateTime.UtcNow, null, null);
        Assert.False(filme.DubladoIa);

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Contains("\"dubladoIa\":false", JsonSerializer.Serialize(filme, web));
        Assert.Contains("\"dubladoIa\":true", JsonSerializer.Serialize(filme with { DubladoIa = true }, web));

        var faixa = new AudioInfo(2, "aac", "por", true, true, "Português (IA)", true);
        var json = JsonSerializer.Serialize(faixa, web);
        Assert.Contains("\"titulo\":\"Portugu", json);
        Assert.Contains("\"dubladoIa\":true", json);

        var antiga = new AudioInfo(1, "ac3", "por", false, false);   // construtor de antes continua valendo
        Assert.Null(antiga.Titulo);
        Assert.False(antiga.DubladoIa);
    }

    // ─── ponta a ponta: /audios, flag no banco, listagem sem ffprobe ─────────────────

    private const string Video = """{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720}""";
    private const string AudioJpn = """{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"disposition":{"default":1},"tags":{"language":"jpn"}}""";
    private const string AudioIa = """{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"disposition":{"default":0},"tags":{"language":"por","title":"Português (IA)"}}""";

    private const string JsonComum = "{\"streams\":[" + Video + "," + AudioJpn + "],\"format\":{\"duration\":\"1400.0\"}}";
    private const string JsonComIa = "{\"streams\":[" + Video + "," + AudioJpn + "," + AudioIa + "],\"format\":{\"duration\":\"1400.0\"}}";

    /// <summary>ffprobe falso: arquivo cujo conteúdo contém "DUBLADO" tem a faixa IA; "QUEBRADO" faz o ffprobe falhar. Cada chamada é anotada em
    /// <c>ffprobe-ia.sh.chamadas</c>.</summary>
    private string FfprobeIa() => TestesComuns.ScriptFalso(_bin, "ffprobe-ia.sh", $$"""
        for a; do arq="$a"; done
        echo "$arq" >> "$0.chamadas"
        grep -q QUEBRADO "$arq" && exit 1
        if grep -q DUBLADO "$arq"; then
          echo '{{JsonComIa}}'
        else
          echo '{{JsonComum}}'
        fi
        """);

    private int Chamadas() => File.Exists(Path.Combine(_bin, "ffprobe-ia.sh.chamadas")) ? File.ReadAllLines(Path.Combine(_bin, "ffprobe-ia.sh.chamadas")).Length : 0;

    private async Task<ScanFixture> Cenario(params (string Arquivo, string Conteudo)[] arquivos)
    {
        var f = new ScanFixture(ffprobe: FfprobeIa());
        _fixtures.Add(f);
        foreach (var (arquivo, conteudo) in arquivos) f.Arquivo(arquivo, conteudo);
        await f.ScanAsync();
        return f;
    }

    private static DubladoIaService Servico(ScanFixture f, RegistroDeTarefas? tarefas = null, Dictionary<string, string?>? extra = null) =>
        new(f.Escopos, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["MediaPath"] = f.Media }.Concat(extra ?? []).ToDictionary(kv => kv.Key, kv => kv.Value)).Build(),
            NullLogger<DubladoIaService>.Instance, tarefas);

    private static Task<List<FilmeResponse>> Catalogo(ScanFixture f) =>
        f.NoEscopoAsync(sp => sp.GetRequiredService<FilmeService>().ListarAsync());

    private static Task<List<AnaliseDeDublagem>> Linhas(ScanFixture f) =>
        f.NoEscopoAsync(sp => sp.GetRequiredService<AppDbContext>().AnalisesDeDublagem.AsNoTracking().OrderBy(a => a.FilmeId).ToListAsync());

    [Fact]
    public async Task Audios_do_arquivo_trazem_titulo_e_dubladoIa_e_a_ia_so_e_a_atual_quando_e_a_unica_em_portugues()
    {
        var f = await Cenario(("Anime/Galaxy Express 999 - 01.mkv", "DUBLADO"));
        var id = await f.IdDeAsync("Anime/Galaxy Express 999 - 01.mkv");

        var audios = await f.Servico<HlsTranscodeService>().ListarAudiosAsync(id, Path.Combine(f.Media, "Anime/Galaxy Express 999 - 01.mkv"), CancellationToken.None);

        Assert.Equal(2, audios.Count);
        var original = audios.Single(a => a.Idioma == "jpn");
        var ia = audios.Single(a => a.Idioma == "por");
        Assert.False(original.DubladoIa);
        Assert.Null(original.Titulo);
        Assert.True(ia.DubladoIa);
        Assert.Equal("Português (IA)", ia.Titulo);
        Assert.True(ia.Atual);          // única em português: ganha do original que é a default do arquivo
        Assert.False(original.Atual);
    }

    [Fact]
    public async Task Listagem_nao_roda_ffprobe_e_so_marca_depois_da_analise_em_segundo_plano()
    {
        var f = await Cenario(("Filmes/Normal.mkv", "comum"), ("Anime/Dublado.mkv", "DUBLADO"));

        // antes da análise: nada marcado, e listar não toca no ffprobe
        for (var i = 0; i < 3; i++) Assert.All(await Catalogo(f), x => Assert.False(x.DubladoIa));
        Assert.Equal(0, Chamadas());

        var servico = Servico(f);
        Assert.Equal(2, await servico.ExecutarUmaVezAsync());
        Assert.Equal(2, Chamadas());   // um ffprobe por arquivo, na análise

        // depois: o flag vem do banco; listar de novo não gasta ffprobe
        for (var i = 0; i < 3; i++)
        {
            var catalogo = await Catalogo(f);
            Assert.True(catalogo.Single(x => x.ArquivoPath == "Anime/Dublado.mkv").DubladoIa);
            Assert.False(catalogo.Single(x => x.ArquivoPath == "Filmes/Normal.mkv").DubladoIa);
        }
        Assert.Equal(2, Chamadas());
        Assert.Equal([false, true], (await Linhas(f)).Select(a => a.DubladoIa).OrderBy(b => b));
    }

    [Fact]
    public async Task Arquivo_ja_analisado_nao_e_lido_de_novo_ate_o_tamanho_mudar()
    {
        var f = await Cenario(("Anime/Ep.mkv", "comum"));
        var servico = Servico(f);
        Assert.Equal(1, await servico.ExecutarUmaVezAsync());
        Assert.False((await Catalogo(f)).Single().DubladoIa);

        Assert.Equal(0, await servico.ExecutarUmaVezAsync());
        Assert.Equal(1, Chamadas());

        // o scripts/dublar troca o arquivo por uma versão com a faixa IA (outro tamanho)
        f.Arquivo("Anime/Ep.mkv", "DUBLADO com a faixa nova, bem maior");
        Assert.Equal(1, await servico.ExecutarUmaVezAsync());
        Assert.True((await Catalogo(f)).Single().DubladoIa);
        Assert.Equal(2, Chamadas());

        Assert.Equal(0, await servico.ExecutarUmaVezAsync());   // de novo em dia
        Assert.Equal(2, Chamadas());
    }

    [Fact]
    public async Task Backfill_respeita_o_lote_e_continua_na_rodada_seguinte()
    {
        var f = await Cenario(("A.mkv", "DUBLADO a"), ("B.mkv", "comum b"), ("C.mkv", "comum c"));
        var servico = Servico(f, extra: new() { ["DubladoIaLoteMax"] = "2" });

        Assert.Equal(2, await servico.ExecutarUmaVezAsync());
        Assert.Equal(1, servico.Pendentes);
        Assert.Equal(1, await servico.ExecutarUmaVezAsync());
        Assert.Equal(0, servico.Pendentes);
        Assert.Equal(3, (await Linhas(f)).Count);
        Assert.Equal(3, Chamadas());
    }

    [Fact]
    public async Task Ffprobe_que_falha_nao_grava_nada_e_nao_e_repetido_na_hora()
    {
        var f = await Cenario(("Q.mkv", "QUEBRADO"), ("Anime/Ok.mkv", "DUBLADO"));
        var servico = Servico(f);

        Assert.Equal(1, await servico.ExecutarUmaVezAsync());
        Assert.Equal(0, servico.Pendentes);   // o que falhou fica de molho, não conta como fila
        Assert.Equal(["Anime/Ok.mkv"], (await Catalogo(f)).Where(x => x.DubladoIa).Select(x => x.ArquivoPath));
        Assert.Single(await Linhas(f));
        var antes = Chamadas();

        Assert.Equal(0, await servico.ExecutarUmaVezAsync());
        Assert.Equal(antes, Chamadas());      // não martela o arquivo que acabou de falhar
    }

    [Fact]
    public async Task Rodada_resume_o_progresso_na_tarefa_do_status()
    {
        var f = await Cenario(("Anime/Ep.mkv", "DUBLADO"));
        var tarefas = new RegistroDeTarefas();
        var servico = Servico(f, tarefas);

        await servico.ExecutarUmaVezAsync();

        var t = Assert.Single(tarefas.Snapshot());
        Assert.Equal(("dublado-ia", "ok"), (t.Nome, t.Estado));
        Assert.Contains("1 com dublagem IA", t.Resumo);
        Assert.Contains("0 pendente(s)", t.Resumo);
    }

    [Fact]
    public async Task Intervalo_zero_declara_a_tarefa_desligada()
    {
        var tarefas = new RegistroDeTarefas();
        var servico = new DubladoIaService(null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DubladoIaIntervaloMinutos"] = "0" }).Build(),
            NullLogger<DubladoIaService>.Instance, tarefas);
        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!;
        Assert.Equal("desligada", Assert.Single(tarefas.Snapshot()).Estado);
    }

    [Fact]
    public async Task Arquivo_removido_do_catalogo_leva_a_analise_junto()
    {
        var f = await Cenario(("A.mkv", "DUBLADO a"), ("B.mkv", "comum b"));
        await Servico(f).ExecutarUmaVezAsync();
        Assert.Equal(2, (await Linhas(f)).Count);

        File.Delete(Path.Combine(f.Media, "A.mkv"));
        await f.ScanAsync();   // some do catálogo; ON DELETE CASCADE leva a linha

        Assert.Equal(["B.mkv"], (await Catalogo(f)).Select(x => x.ArquivoPath));
        Assert.Single(await Linhas(f));
    }

    // ─── esquema: banco antigo ganha a tabela ────────────────────────────────────────

    [Fact]
    public async Task Banco_antigo_ganha_a_tabela_de_analise_de_dublagem()
    {
        var arq = Path.Combine(TestesComuns.CriarPastaTemp(), "velho.db");
        var cs = $"Data Source={arq}";
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Filmes" ("Id" INTEGER PRIMARY KEY, "Titulo" TEXT NOT NULL, "ArquivoPath" TEXT NULL, "Assistido" INTEGER NOT NULL, "DataAdicionado" TEXT NOT NULL);
                INSERT INTO "Filmes" VALUES (1, 'x', 'a.mkv', 0, '2026-01-01');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).Options);
        DbInitializer.Executar(db);
        DbInitializer.Executar(db);   // idempotente

        Assert.Equal(0, await db.AnalisesDeDublagem.CountAsync());
        db.AnalisesDeDublagem.Add(new AnaliseDeDublagem { FilmeId = 1, DubladoIa = true, ArquivoBytes = 123 });
        await db.SaveChangesAsync();
        Assert.True((await db.AnalisesDeDublagem.AsNoTracking().SingleAsync()).DubladoIa);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
