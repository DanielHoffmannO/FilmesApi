using FilmesApi.Data;
using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FilmesApi.Tests;

/// <summary>Legenda padrão por série (junto do áudio, na mesma linha) e interruptores por temporada.</summary>
public class PreferenciasPadraoTests
{
    [Fact]
    public void Legenda_da_serie_grava_le_de_volta_e_sobrevive_a_um_servico_novo()
    {
        using var f = new ScanFixture();
        Assert.True(f.Prefs.DefinirLegendaDaSerie("o mentalista", "eng"));
        Assert.True(f.Prefs.DefinirLegendaDaSerie("rick and morty", "off"));

        var outro = new PreferenciasAudioService(f.Escopos);   // como depois de um restart
        Assert.Equal("eng", outro.LegendaDaSerie("o mentalista"));
        Assert.Equal("off", outro.LegendaDaSerie("rick and morty"));
        Assert.Null(outro.LegendaDaSerie("outra"));
    }

    [Fact]
    public void Idioma_invalido_e_recusado_e_null_volta_ao_automatico()
    {
        using var f = new ScanFixture();
        Assert.False(f.Prefs.DefinirLegendaDaSerie("x", "klingon"));
        Assert.True(f.Prefs.DefinirLegendaDaSerie("x", "por"));
        Assert.True(f.Prefs.DefinirLegendaDaSerie("x", null));
        Assert.Null(f.Prefs.LegendaDaSerie("x"));
    }

    [Fact]
    public async Task Audio_e_legenda_sao_independentes_e_a_linha_some_so_quando_nao_sobra_nada()
    {
        using var f = new ScanFixture();
        f.Prefs.DefinirIdiomaDaSerie("s", "eng");
        f.Prefs.DefinirLegendaDaSerie("s", "por");

        f.Prefs.DefinirIdiomaDaSerie("s", null);   // tira só o áudio
        var depois = new PreferenciasAudioService(f.Escopos);
        Assert.Null(depois.IdiomaDaSerie("s"));
        Assert.Empty(depois.TodasAsSeries());      // legenda sozinha não vira "áudio vazio"
        Assert.Equal("por", depois.LegendaDaSerie("s"));

        f.Prefs.DefinirLegendaDaSerie("s", null);  // agora não sobra nada
        var linhas = await f.NoEscopoAsync(sp => Task.FromResult(sp.GetRequiredService<AppDbContext>().PreferenciasSerie.Count()));
        Assert.Equal(0, linhas);
    }

    [Fact]
    public void Legenda_so_com_audio_definido_depois_tambem_funciona()
    {
        using var f = new ScanFixture();
        f.Prefs.DefinirLegendaDaSerie("s", "off");
        f.Prefs.DefinirIdiomaDaSerie("s", "eng");
        var outro = new PreferenciasAudioService(f.Escopos);
        Assert.Equal(("eng", "off"), (outro.IdiomaDaSerie("s"), outro.LegendaDaSerie("s")));
    }

    // ─── a escolha da legenda quando a série tem padrão ──────────────────────────────

    private static LegendaInfo Faixa(int idx, string? idioma, bool forced = false, bool convertivel = true) =>
        new(idx, convertivel ? "subrip" : "hdmv_pgs_subtitle", idioma, null, forced, false, convertivel);

    [Fact]
    public void Padrao_off_nao_liga_nada_e_idioma_pega_a_melhor_faixa_dele()
    {
        var faixas = SubtitleService.OrdenarPorRelevancia([Faixa(0, "eng", forced: true), Faixa(1, "eng"), Faixa(2, "por")]);
        Assert.Null(SubtitleService.LegendaPorPadraoDaSerie("off", faixas));
        Assert.Equal(1, SubtitleService.LegendaPorPadraoDaSerie("eng", faixas));   // a forçada não vale
        Assert.Equal(2, SubtitleService.LegendaPorPadraoDaSerie("por", faixas));
        Assert.Null(SubtitleService.LegendaPorPadraoDaSerie("spa", faixas));
    }

    [Fact]
    public void Faixa_bitmap_nao_e_escolhida() =>
        Assert.Null(SubtitleService.LegendaPorPadraoDaSerie("eng", [Faixa(0, "eng", convertivel: false)]));

    // ─── interruptores por temporada ─────────────────────────────────────────────────

    [Fact]
    public void Temporada_comeca_desligada_liga_parcial_e_persiste()
    {
        using var f = new ScanFixture();
        var t = new PreferenciasTemporadaService(f.Escopos);
        Assert.Equal(new PreferenciasTemporadaService.Ligados(false, false), t.Da("mentalista", 4));

        t.Definir("mentalista", 4, pularAbertura: true, proximoAutomatico: null);
        t.Definir("mentalista", 4, pularAbertura: null, proximoAutomatico: true);
        t.Definir("mentalista", 5, pularAbertura: true, proximoAutomatico: null);
        t.Definir("mentalista", 5, pularAbertura: false, proximoAutomatico: null);

        var depois = new PreferenciasTemporadaService(f.Escopos);
        Assert.Equal(new PreferenciasTemporadaService.Ligados(true, true), depois.Da("mentalista", 4));
        Assert.Equal(new PreferenciasTemporadaService.Ligados(false, false), depois.Da("mentalista", 5));
        Assert.Equal(new PreferenciasTemporadaService.Ligados(false, false), depois.Da("outra", 4));
        Assert.Equal(["4", "5"], depois.Todas()["mentalista"].Keys.Order());
    }

    // ─── banco antigo ────────────────────────────────────────────────────────────────

    [Fact]
    public void Banco_antigo_ganha_a_coluna_de_legenda_e_a_tabela_de_temporadas_sem_perder_dados()
    {
        var arq = Path.Combine(TestesComuns.CriarPastaTemp(), "velho.db");
        var cs = $"Data Source={arq}";
        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Filmes" ("Id" INTEGER PRIMARY KEY, "Titulo" TEXT NOT NULL, "ArquivoPath" TEXT NULL, "Assistido" INTEGER NOT NULL, "DataAdicionado" TEXT NOT NULL);
                CREATE TABLE "PreferenciasSerie" ("ChaveSerie" TEXT NOT NULL CONSTRAINT "PK_PreferenciasSerie" PRIMARY KEY, "IdiomaAudio" TEXT NOT NULL);
                INSERT INTO "PreferenciasSerie" VALUES ('o mentalista', 'eng');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).Options);
        DbInitializer.Executar(db);
        DbInitializer.Executar(db);   // idempotente

        var linha = db.PreferenciasSerie.AsNoTracking().Single();
        Assert.Equal(("eng", null), (linha.IdiomaAudio, linha.IdiomaLegenda));
        db.PreferenciasTemporada.Add(new PreferenciaTemporada { ChaveSerie = "x", Temporada = 1, PularAbertura = true });
        db.SaveChanges();
        SqliteConnection.ClearAllPools();
    }
}
