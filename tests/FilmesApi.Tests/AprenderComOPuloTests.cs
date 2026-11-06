using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmesApi.Tests;

/// <summary>A rajada de toques « 30s » do controle e o que o servidor faz com ela: confere se era a abertura, grava, acha nos outros episódios e liga o pulo automático.</summary>
public class RajadaDePuloTests
{
    private sealed class Relogio { public DateTime Agora = new(2026, 11, 6, 20, 0, 0, DateTimeKind.Utc); public void Passa(double s) => Agora = Agora.AddSeconds(s); }

    private static (PlayerStateService Svc, Relogio R, List<RajadaDePulo> Rajadas) Cenario()
    {
        var r = new Relogio();
        var svc = new PlayerStateService(() => r.Agora);
        var rajadas = new List<RajadaDePulo>();
        svc.RajadaConcluida += rajadas.Add;
        svc.Selecionar(10);
        svc.ReportarPosicao(100, 1300);
        return (svc, r, rajadas);
    }

    /// <summary>A TV reporta a cada 2 s; simula os reports durante `segundos` (a posição avança 1 s por s).</summary>
    private static void Reports(PlayerStateService svc, Relogio r, double posInicial, double segundos)
    {
        for (var t = 2.0; t <= segundos; t += 2) { r.Passa(2); svc.ReportarPosicao(posInicial + t, 1300); }
    }

    [Fact]
    public void Toques_proximos_viram_uma_rajada_e_a_posicao_final_vem_do_report_da_TV()
    {
        var (svc, r, rajadas) = Cenario();
        svc.Seek(30); r.Passa(2); svc.Seek(30); r.Passa(2); svc.Seek(30);   // 3 toques em 4 s: 100 -> ~190
        Reports(svc, r, 190, 10);   // a TV pousou em 190 e segue tocando; passados 6 s do último toque a rajada fecha

        var rajada = Assert.Single(rajadas);
        Assert.Equal(10, rajada.FilmeId);
        Assert.Equal(100, rajada.PosAntes);
        Assert.InRange(rajada.PosFinal, 190, 200);
    }

    [Fact]
    public void Toques_separados_por_mais_de_6_s_sao_rajadas_diferentes()
    {
        var (svc, r, rajadas) = Cenario();
        svc.Seek(30);
        Reports(svc, r, 130, 10);   // fecha a 1ª
        svc.ReportarPosicao(300, 1300);
        svc.Seek(30);
        Reports(svc, r, 330, 10);

        Assert.Equal(2, rajadas.Count);
        Assert.Equal(100, rajadas[0].PosAntes);
        Assert.Equal(300, rajadas[1].PosAntes);
    }

    [Fact]
    public void Recuo_no_meio_descarta_a_rajada_e_so_recuo_nunca_gera_nada()
    {
        var (svc, r, rajadas) = Cenario();
        svc.Seek(30); r.Passa(1); svc.Seek(-30);
        Reports(svc, r, 100, 12);
        Assert.Empty(rajadas);

        svc.Seek(-30);
        Reports(svc, r, 100, 12);
        Assert.Empty(rajadas);
    }

    [Fact]
    public void Sem_filme_tocando_nao_ha_rajada()
    {
        var r = new Relogio();
        var svc = new PlayerStateService(() => r.Agora);
        var rajadas = new List<RajadaDePulo>();
        svc.RajadaConcluida += rajadas.Add;
        svc.Seek(30);
        for (var i = 0; i < 6; i++) { r.Passa(2); svc.ReportarPosicao(200, 0); }
        Assert.Empty(rajadas);
    }

    [Fact]
    public void Trocar_de_episodio_no_meio_descarta_a_rajada()
    {
        var (svc, r, rajadas) = Cenario();
        svc.Seek(30);
        svc.Selecionar(11);
        Reports(svc, r, 0, 12);
        Assert.Empty(rajadas);
    }

    [Fact]
    public void Aviso_sobe_a_versao_e_o_contexto_vai_no_snapshot()
    {
        var (svc, _, _) = Cenario();
        svc.DefinirContexto(10, new ContextoDoFilme(100, 190, true, false, null, "serie", 3));
        svc.DefinirContexto(99, new ContextoDoFilme(1, 2, false, false, null, "outra", 1));   // outro filme: ignorado
        svc.Avisar("abertura", "Abertura pulada", "voltar");
        dynamic s = svc.Snapshot();

        Assert.Equal(100.0, s.abertura.inicio);
        Assert.Equal(190.0, s.abertura.fim);
        Assert.True(s.abertura.pular);
        Assert.False(s.abertura.pulada);
        Assert.Equal((1, "abertura", "voltar"), (s.aviso.versao, s.aviso.tipo, s.aviso.acao));

        svc.MarcarAberturaPulada();
        dynamic depois = svc.Snapshot();
        Assert.True(depois.abertura.pulada);

        svc.Selecionar(11);   // novo episódio zera o contexto
        dynamic zerado = svc.Snapshot();
        Assert.Null(zerado.abertura);
    }
}

public class AprendizadoDePuloTests
{
    private static int[] Aleatorio(int n, int semente) { var r = new Random(semente); return Enumerable.Range(0, n).Select(_ => r.Next(int.MinValue, int.MaxValue)).ToArray(); }
    private static int Itens(double s) => (int)(s / DetectorDeAbertura.SegundosPorItem);

    [Fact]
    public void Janela_tem_folga_de_15_s_dos_dois_lados_e_nao_passa_de_zero()
    {
        Assert.Equal((585.0, 150.0), AprendizadoDePulo.Janela(new RajadaDePulo(1, 600, 720)));
        Assert.Equal((0.0, 125.0), AprendizadoDePulo.Janela(new RajadaDePulo(1, 5, 110)));
    }

    [Theory]
    [InlineData(100, 190, 90, 190, true)]    // o pulo cobre tudo
    [InlineData(100, 190, 140, 300, true)]   // cobre metade (50 de 90)
    [InlineData(100, 190, 170, 300, false)]  // cobre só 20 s
    public void Pulo_que_cobre_a_abertura_conhecida_so_confirma(double ini, double fim, double de, double ate, bool cobre) =>
        Assert.Equal(cobre, AprendizadoDePulo.CobreAConhecida(new RajadaDePulo(1, de, ate), ini, fim));

    [Fact]
    public void Valida_o_trecho_quando_algum_vizinho_tem_o_mesmo_audio_mesmo_em_outro_ponto()
    {
        var abertura = Aleatorio(Itens(40), 1);
        // janela do episódio: começa em 585 s; a abertura fica 15 s dentro dela (= 600 s no episódio)
        var janela = Aleatorio(Itens(15), 2).Concat(abertura).Concat(Aleatorio(Itens(60), 3)).ToArray();
        var vizinho = Aleatorio(Itens(100), 4).Concat(abertura).Concat(Aleatorio(Itens(300), 5)).ToArray();   // abertura aos 100 s
        var r = new RajadaDePulo(1, 598, 650);

        var t = AprendizadoDePulo.Validar(janela, 585, r, [Aleatorio(Itens(400), 6), vizinho], minimoSegundos: 4);

        Assert.NotNull(t);
        Assert.InRange(t!.InicioA, 598, 603);   // ~600 s no episódio
        Assert.InRange(t.Duracao, 38, 48);
    }

    [Fact]
    public void Abertura_de_7_s_estilo_Futurama_valida_com_o_pulo_e_o_modelo_acha_nos_outros_episodios()
    {
        var abertura = Aleatorio(Itens(7), 1);   // abaixo do mínimo da varredura (10 s): só o aprendizado pelo pulo (mínimo 4 s) cobre
        var janela = Aleatorio(Itens(20), 2).Concat(abertura).Concat(Aleatorio(Itens(40), 3)).ToArray();   // janela começa em 0: abertura aos 20–27 s
        var vizinho = Aleatorio(Itens(35), 4).Concat(abertura).Concat(Aleatorio(Itens(200), 5)).ToArray();
        var r = new RajadaDePulo(1, 18, 30);   // ele pulou de 18 s a 30 s

        var validado = AprendizadoDePulo.Validar(janela, 0, r, [vizinho], 4);

        Assert.NotNull(validado);
        Assert.InRange(validado!.InicioA, 18, 21);
        Assert.InRange(validado.Duracao, 5.5, 10);
        var outros = new[] { Aleatorio(Itens(70), 6).Concat(abertura).Concat(Aleatorio(Itens(200), 7)).ToArray(),
                             Aleatorio(Itens(10), 8).Concat(abertura).Concat(Aleatorio(Itens(200), 9)).ToArray() };
        var inicios = new[] { 70.0, 10.0 };
        for (var i = 0; i < outros.Length; i++)
        {
            var achado = AprendizadoDePulo.ProcurarModelo(janela, 0, validado, outros[i], minimoPuloSegundos: 4);
            Assert.NotNull(achado);
            Assert.InRange(achado!.Value.Inicio, inicios[i] - 3, inicios[i] + 1);
        }
    }

    [Fact]
    public void Cena_pulada_sem_audio_em_comum_nao_e_abertura()
    {
        var janela = Aleatorio(Itens(120), 2);
        var r = new RajadaDePulo(1, 20, 100);
        Assert.Null(AprendizadoDePulo.Validar(janela, 0, r, [Aleatorio(Itens(400), 6), Aleatorio(Itens(400), 7)], 4));
    }

    [Fact]
    public void Trecho_comum_longe_do_que_foi_pulado_nao_vale()
    {
        var comum = Aleatorio(Itens(20), 1);
        var janela = comum.Concat(Aleatorio(Itens(100), 2)).ToArray();   // o áudio comum está no começo da janela (0–20 s)
        var vizinho = Aleatorio(Itens(50), 3).Concat(comum).ToArray();
        var r = new RajadaDePulo(1, 90, 118);   // ...mas ele pulou do 90 ao 118
        Assert.Null(AprendizadoDePulo.Validar(janela, 0, r, [vizinho], 4));
    }

    [Fact]
    public void Modelo_acha_o_mesmo_audio_em_outro_episodio_sem_precisar_de_10_s()
    {
        var abertura = Aleatorio(Itens(8), 1);   // abertura curta de 8 s: o mínimo da varredura (10 s) a perderia
        var janela = Aleatorio(Itens(15), 2).Concat(abertura).Concat(Aleatorio(Itens(30), 3)).ToArray();
        var validado = new TrechoComum(100 + 15, 100 + 23, 0, 0);   // janela começa em 100 s: a abertura está em 115–123 s
        var outro = Aleatorio(Itens(60), 4).Concat(abertura).Concat(Aleatorio(Itens(200), 5)).ToArray();

        var achado = AprendizadoDePulo.ProcurarModelo(janela, 100, validado, outro, minimoPuloSegundos: 4);

        Assert.NotNull(achado);
        Assert.InRange(achado!.Value.Inicio, 59, 61);
        Assert.InRange(achado.Value.Fim, 67, 69);
        Assert.Null(AprendizadoDePulo.ProcurarModelo(janela, 100, validado, Aleatorio(Itens(300), 9), 4));
    }
}

/// <summary>De ponta a ponta com ffmpeg FALSO (devolve o "&lt;arquivo&gt;.fp"): a janela do episódio pulado é o .fp dele; os vizinhos são o começo (480 s) deles.</summary>
public class AutomacaoDoPlayerTests : IDisposable
{
    private readonly string _bin = TestesComuns.CriarPastaTemp();
    private readonly List<ScanFixture> _fixtures = [];
    public void Dispose() { foreach (var f in _fixtures) f.Dispose(); Directory.Delete(_bin, recursive: true); }

    private static int[] Aleatorio(int n, int semente) { var r = new Random(semente); return Enumerable.Range(0, n).Select(_ => r.Next(int.MinValue, int.MaxValue)).ToArray(); }
    private static int Itens(double s) => (int)(s / DetectorDeAbertura.SegundosPorItem);
    private static void Gravar(string arq, int[] itens) => File.WriteAllBytes(arq + ".fp", ArmazemDeImpressoes.Codificar(itens));

    private static readonly string[] Eps =
        ["Futurama/Futurama.S04E27.mkv", "Futurama/Futurama.S04E28.mkv", "Futurama/Futurama.S04E29.mkv", "Futurama/Futurama.S04E30.mkv"];

    private async Task<(ScanFixture F, AutomacaoDoPlayer A, PlayerStateService Estado, PreferenciasTemporadaService Temp)> Cenario(params string[] arquivos)
    {
        var ffmpeg = TestesComuns.ScriptFalso(_bin, "ffmpeg-fp.sh", "while [ $# -gt 0 ]; do [ \"$1\" = \"-i\" ] && arq=\"$2\"; shift; done\n[ -f \"$arq.fp\" ] || exit 1\ncat \"$arq.fp\"");
        var f = new ScanFixture(ffmpeg: ffmpeg);
        _fixtures.Add(f);
        foreach (var e in arquivos) f.Arquivo(e, new string('x', 2048));
        await f.ScanAsync();
        var estado = new PlayerStateService();
        var temp = new PreferenciasTemporadaService(f.Escopos);
        var a = new AutomacaoDoPlayer(f.Escopos, estado, temp, f.Servico<FfmpegOptions>(), new ConfigurationBuilder().Build(), NullLogger<AutomacaoDoPlayer>.Instance);
        return (f, a, estado, temp);
    }

    private static Task<Dictionary<int, AberturaEpisodio>> Linhas(ScanFixture f) =>
        f.NoEscopoAsync(async sp => (await sp.GetRequiredService<AppDbContext>().Aberturas.AsNoTracking().ToListAsync()).ToDictionary(a => a.FilmeId));

    [Fact]
    public async Task Pulo_da_abertura_fora_dos_480_s_vira_abertura_do_episodio_dos_outros_e_liga_o_pulo_automatico()
    {
        var (f, a, estado, temp) = await Cenario(Eps);
        var abertura = Aleatorio(Itens(40), 1);
        // episódio pulado (E29): abertura aos 600 s. O .fp dele é a JANELA extraída (começa em 585 s): 15 s de cena e a abertura.
        Gravar(Path.Combine(f.Media, Eps[2]), Aleatorio(Itens(15), 2).Concat(abertura).Concat(Aleatorio(Itens(60), 3)).ToArray());
        // os outros: abertura aos 100/120/80 s dos primeiros 480 s
        var inicios = new[] { 100, 120, 0, 80 };
        for (var i = 0; i < Eps.Length; i++)
            if (i != 2) Gravar(Path.Combine(f.Media, Eps[i]), Aleatorio(Itens(inicios[i]), 10 + i).Concat(abertura).Concat(Aleatorio(Itens(300), 20 + i)).ToArray());
        var id = await f.IdDeAsync(Eps[2]);
        estado.Selecionar(id);

        await a.AprenderAsync(new RajadaDePulo(id, 598, 650));

        var l = await Linhas(f);
        Assert.Equal(4, l.Count);
        Assert.All(l.Values, x => Assert.Equal(OrigemMarca.Pulo, x.Origem));
        Assert.InRange(l[id].InicioSegundos!.Value, 594, 603);   // as bordas têm ~±3 s (o critério folgado estende um pouco)
        for (var i = 0; i < Eps.Length; i++)
            if (i != 2) Assert.InRange(l[await f.IdDeAsync(Eps[i])].InicioSegundos!.Value, inicios[i] - 6, inicios[i] + 1);
        Assert.True(temp.Da("futurama", 4).PularAbertura);
        Assert.False(temp.Da("futurama", 4).ProximoAutomatico);   // só liga o que foi aprendido
        dynamic s = estado.Snapshot();
        Assert.Equal("abertura", s.aviso.tipo);
        Assert.Equal("desligar", s.aviso.acao);
        Assert.Contains("temporada 4", (string)s.aviso.texto);
    }

    [Fact]
    public async Task So_os_vizinhos_ate_2_de_distancia_tem_a_impressao_gerada_na_hora()
    {
        string[] seis = [.. Eps, "Futurama/Futurama.S04E31.mkv", "Futurama/Futurama.S04E32.mkv"];
        var (f, a, estado, _) = await Cenario(seis);
        var abertura = Aleatorio(Itens(40), 1);
        Gravar(Path.Combine(f.Media, seis[0]), Aleatorio(Itens(10), 2).Concat(abertura).Concat(Aleatorio(Itens(60), 3)).ToArray());   // janela do pulado (E27)
        for (var i = 1; i < seis.Length; i++) Gravar(Path.Combine(f.Media, seis[i]), Aleatorio(Itens(100), 10 + i).Concat(abertura).Concat(Aleatorio(Itens(300), 20 + i)).ToArray());
        var id0 = await f.IdDeAsync(seis[0]);
        estado.Selecionar(id0);

        await a.AprenderAsync(new RajadaDePulo(id0, 5, 55));

        var l = await Linhas(f);
        Assert.Equal(new[] { id0, await f.IdDeAsync(seis[1]), await f.IdDeAsync(seis[2]) }.Order(), l.Keys.Order());   // E27 + ±2; o resto fica pro serviço de fundo
        var geradas = await f.NoEscopoAsync(sp => sp.GetRequiredService<AppDbContext>().Impressoes.AsNoTracking().Select(x => x.FilmeId).ToListAsync());
        Assert.DoesNotContain(await f.IdDeAsync(seis[3]), geradas);
        Assert.DoesNotContain(await f.IdDeAsync(seis[5]), geradas);
        dynamic s = estado.Snapshot();
        Assert.Contains("aprendida", (string)s.aviso.texto);   // o toast "Abertura aprendida" continua saindo
    }

    [Fact]
    public async Task Impressao_ja_guardada_de_episodio_distante_ainda_serve_de_modelo()
    {
        string[] seis = [.. Eps, "Futurama/Futurama.S04E31.mkv", "Futurama/Futurama.S04E32.mkv"];
        var (f, a, estado, _) = await Cenario(seis);
        var abertura = Aleatorio(Itens(40), 1);
        Gravar(Path.Combine(f.Media, seis[0]), Aleatorio(Itens(10), 2).Concat(abertura).Concat(Aleatorio(Itens(60), 3)).ToArray());
        for (var i = 1; i < seis.Length; i++) Gravar(Path.Combine(f.Media, seis[i]), Aleatorio(Itens(100), 10 + i).Concat(abertura).Concat(Aleatorio(Itens(300), 20 + i)).ToArray());
        var idLonge = await f.IdDeAsync(seis[5]);
        await f.NoEscopoAsync(async sp =>   // o serviço de fundo já tinha guardado a impressão dele
        {
            var db = sp.GetRequiredService<AppDbContext>();
            await ArmazemDeImpressoes.ObterAsync(db, sp.GetRequiredService<FfmpegOptions>(), idLonge, Path.Combine(f.Media, seis[5]),
                ArmazemDeImpressoes.JanelaInicio, 0, ImpressaoDeAudio.Trecho.Inicio, CancellationToken.None);
            return 0;
        });
        var id0 = await f.IdDeAsync(seis[0]);
        estado.Selecionar(id0);

        await a.AprenderAsync(new RajadaDePulo(id0, 5, 55));

        Assert.True((await Linhas(f)).ContainsKey(idLonge));
    }

    [Fact]
    public async Task Duas_rajadas_seguidas_nao_rodam_ao_mesmo_tempo()
    {
        var log = Path.Combine(_bin, "ffmpeg.log");
        var ffmpeg = TestesComuns.ScriptFalso(_bin, "ffmpeg-lento.sh",
            $"while [ $# -gt 0 ]; do [ \"$1\" = \"-i\" ] && arq=\"$2\"; shift; done\n[ -f \"$arq.fp\" ] || exit 1\necho ini >> \"{log}\"\nsleep 0.15\ncat \"$arq.fp\"\necho fim >> \"{log}\"");
        var f = new ScanFixture(ffmpeg: ffmpeg);
        _fixtures.Add(f);
        foreach (var e in Eps) f.Arquivo(e, new string('x', 2048));
        await f.ScanAsync();
        var abertura = Aleatorio(Itens(40), 1);
        Gravar(Path.Combine(f.Media, Eps[1]), Aleatorio(Itens(10), 2).Concat(abertura).Concat(Aleatorio(Itens(60), 3)).ToArray());
        foreach (var i in new[] { 0, 2, 3 }) Gravar(Path.Combine(f.Media, Eps[i]), Aleatorio(Itens(100), 10 + i).Concat(abertura).Concat(Aleatorio(Itens(300), 20 + i)).ToArray());
        var a = new AutomacaoDoPlayer(f.Escopos, new PlayerStateService(), new PreferenciasTemporadaService(f.Escopos), f.Servico<FfmpegOptions>(),
            new ConfigurationBuilder().Build(), NullLogger<AutomacaoDoPlayer>.Instance);
        var id = await f.IdDeAsync(Eps[1]);

        await Task.WhenAll(a.AprenderAsync(new RajadaDePulo(id, 5, 55)), a.AprenderAsync(new RajadaDePulo(id, 5, 55)));

        var linhas = await File.ReadAllLinesAsync(log);
        Assert.NotEmpty(linhas);
        for (var i = 0; i < linhas.Length; i++) Assert.Equal(i % 2 == 0 ? "ini" : "fim", linhas[i]);   // nunca dois ffmpeg em paralelo
    }

    [Fact]
    public async Task Ligar_o_pulo_automatico_concorrente_gera_um_unico_aviso()
    {
        var (f, a, estado, temp) = await Cenario(Eps);
        var id = await f.IdDeAsync(Eps[1]);

        // o "ligar" em si (leitura + gravação sob a trava do serviço): 8 threads, uma só ganha
        var ganhou = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8).Select(_ => temp.Ligar("futurama", 4, abertura: true)).ToList();
        Assert.Single(ganhou, true);
        Assert.True(temp.Da("futurama", 4).PularAbertura);
        Assert.False(temp.Da("futurama", 4).ProximoAutomatico);   // ligar um não mexe no outro

        // e quem usa o resultado (aviso) só avisa se foi ele quem ligou
        Assert.True(await a.LigarPuloAutomaticoAsync(id) == false);
        Assert.Null(((dynamic)estado.Snapshot()).aviso);
        Assert.True(await a.LigarProximoAutomaticoAsync(id));
        Assert.False(await a.LigarProximoAutomaticoAsync(id));
        Assert.Equal(1, ((dynamic)estado.Snapshot()).aviso.versao);
    }

    [Fact]
    public async Task Cena_pulada_sem_audio_em_comum_nao_muda_nada()
    {
        var (f, a, estado, temp) = await Cenario(Eps);
        for (var i = 0; i < Eps.Length; i++) Gravar(Path.Combine(f.Media, Eps[i]), Aleatorio(Itens(300), 10 + i));
        var id = await f.IdDeAsync(Eps[1]);
        estado.Selecionar(id);

        await a.AprenderAsync(new RajadaDePulo(id, 100, 160));

        Assert.Empty(await Linhas(f));
        Assert.False(temp.Da("futurama", 4).PularAbertura);
        dynamic s = estado.Snapshot();
        Assert.Null(s.aviso);
    }

    [Fact]
    public async Task Temporada_de_um_episodio_so_e_ignorada()
    {
        var (f, a, _, temp) = await Cenario(Eps[0]);
        Gravar(Path.Combine(f.Media, Eps[0]), Aleatorio(Itens(300), 1));
        await a.AprenderAsync(new RajadaDePulo(await f.IdDeAsync(Eps[0]), 10, 70));
        Assert.Empty(await Linhas(f));
        Assert.False(temp.Da("futurama", 4).PularAbertura);
    }

    [Fact]
    public async Task Pulo_sobre_a_abertura_ja_conhecida_so_confirma_e_liga_sem_reanalisar()
    {
        var (f, a, estado, temp) = await Cenario(Eps);   // nenhum .fp: o ffmpeg falso falharia se fosse chamado
        var id = await f.IdDeAsync(Eps[1]);
        await f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Aberturas.Add(new AberturaEpisodio { FilmeId = id, InicioSegundos = 100, FimSegundos = 190, Origem = OrigemMarca.Capitulo, Versao = 2 });
            await db.SaveChangesAsync();
            return 0;
        });
        estado.Selecionar(id);

        await a.AprenderAsync(new RajadaDePulo(id, 95, 200));

        Assert.True(temp.Da("futurama", 4).PularAbertura);
        Assert.Equal(OrigemMarca.Capitulo, (await Linhas(f))[id].Origem);   // não virou 'pulo' (nada foi reanalisado)
        dynamic s = estado.Snapshot();
        Assert.Contains("confirmada", (string)s.aviso.texto);
    }

    [Fact]
    public async Task Marcacao_de_pulo_vale_mais_que_a_de_capitulo_nos_outros_episodios()
    {
        var (f, a, estado, _) = await Cenario(Eps);
        var abertura = Aleatorio(Itens(40), 1);
        Gravar(Path.Combine(f.Media, Eps[0]), Aleatorio(Itens(10), 2).Concat(abertura).Concat(Aleatorio(Itens(60), 3)).ToArray());   // janela do pulado (E27)
        for (var i = 1; i < Eps.Length; i++) Gravar(Path.Combine(f.Media, Eps[i]), Aleatorio(Itens(100), 10 + i).Concat(abertura).Concat(Aleatorio(Itens(300), 20 + i)).ToArray());
        var id1 = await f.IdDeAsync(Eps[1]);
        await f.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Aberturas.Add(new AberturaEpisodio { FilmeId = id1, InicioSegundos = 1, FimSegundos = 3, Origem = OrigemMarca.Capitulo, Versao = 2 });
            await db.SaveChangesAsync();
            return 0;
        });
        var id0 = await f.IdDeAsync(Eps[0]);
        estado.Selecionar(id0);

        await a.AprenderAsync(new RajadaDePulo(id0, 5, 55));

        var linha = (await Linhas(f))[id1];
        Assert.Equal(OrigemMarca.Pulo, linha.Origem);
        Assert.InRange(linha.InicioSegundos!.Value, 94, 101);
    }
}
