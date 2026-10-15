using FilmesApi.Models;
using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>Os casos reais que o dono achou à mão num script contra /api/filmes/tela: Hora da Aventura com
/// buracos, Breaking Bad sem T5E1-8, Twilight Zone com arquivo duplo, episódio em dois arquivos, vídeos de
/// propaganda de uploader.</summary>
public class DiagnosticoCatalogoTests
{
    private const long MB = 1024 * 1024;

    /// <summary>Classifica de verdade (mesmo parser/cache da API) e roda o diagnóstico.</summary>
    private static DiagnosticoResponse Rodar(params (string Caminho, long Bytes)[] arquivos)
    {
        var brutos = arquivos.Select((a, i) =>
            new FilmeResponse(i + 1, Path.GetFileNameWithoutExtension(a.Caminho), null, a.Caminho, false, DateTime.UtcNow,
                null, null, null, null, null, false, false, null, null, null, null, "", "Sem pasta")).ToList();
        var tamanhos = arquivos.ToDictionary(a => a.Caminho, a => (long?)a.Bytes);
        return DiagnosticoCatalogo.Analisar(new ClassificacaoCache().Classificar(brutos), c => tamanhos.GetValueOrDefault(c));
    }

    private static (string, long) Ep(string pasta, string nome) => ($"Series/{pasta}/{nome}", 300 * MB);

    // ─── episódios faltando ──────────────────────────────────────────────────────────

    [Fact]
    public void Hora_de_Aventura_buracos_no_meio_da_temporada()
    {
        const string pasta = "Hora de Aventura";
        var eps = new List<(string, long)>();
        foreach (var n in Enumerable.Range(1, 10).Where(n => n != 5))
            eps.Add(Ep(pasta, $"Hora de Aventura - Temp 05 - Epi {n:00} - Titulo.mkv"));           // falta o 5
        foreach (var n in Enumerable.Range(1, 34).Where(n => n != 15 && n != 33))
            eps.Add(Ep(pasta, $"Hora de Aventura - Temp 07 - Epi {n:00} - Titulo.mkv"));           // faltam 15 e 33
        foreach (var n in Enumerable.Range(1, 20).Where(n => n is < 14 or > 16))
            eps.Add(Ep(pasta, $"Hora de Aventura - Temp 10 - Epi {n:00} - Titulo.mkv"));           // faltam 14-16

        var r = Rodar([.. eps]);

        Assert.Equal(
            [("Hora de Aventura", 5, "E05"), ("Hora de Aventura", 7, "E15, E33"), ("Hora de Aventura", 10, "E14–E16")],
            r.Faltando.Select(f => (f.Serie, f.Temporada, f.Resumo)));
    }

    [Fact]
    public void Temporada_que_comeca_no_meio_conta_o_comeco_como_faltando()
    {
        // Breaking Bad T5: só existem E09..E16 -> faltam E01-E08
        var eps = Enumerable.Range(9, 8).Select(n => Ep("Breaking Bad", $"Breaking.Bad.S05E{n:00}.720p.mkv")).ToArray();

        var f = Assert.Single(Rodar(eps).Faltando);

        Assert.Equal((5, "E01–E08"), (f.Temporada, f.Resumo));
        Assert.Equal(Enumerable.Range(1, 8), f.Episodios);
    }

    [Fact]
    public void Arquivo_duplo_conta_como_dois_episodios()
    {
        // The Twilight Zone: [1x06-07] cobre o 6 e o 7; sem isso o 7 apareceria como faltando
        var r = Rodar(
            Ep("Twilight", "The.Twilight.Zone.[1x01].Shatterday.mkv"),
            Ep("Twilight", "The.Twilight.Zone.[1x02].Chameleon.mkv"),
            Ep("Twilight", "The.Twilight.Zone.[1x03].Dead.Run.mkv"),
            Ep("Twilight", "The.Twilight.Zone.[1x04].Rye.mkv"),
            Ep("Twilight", "The.Twilight.Zone.[1x05].Wordplay.mkv"),
            Ep("Twilight", "The.Twilight.Zone.[1x06-07].Dupla.mkv"),
            Ep("Twilight", "The.Twilight.Zone.[1x08].Fim.mkv"));

        Assert.Empty(r.Faltando);
    }

    [Theory]
    [InlineData("Show.S01E02E03.mkv")]
    [InlineData("Show.S01E02-E03.mkv")]
    [InlineData("Show.S01E02-03.mkv")]
    [InlineData("Show - Episódio 02-03.mkv")]
    public void Varios_formatos_de_arquivo_duplo_cobrem_o_episodio_3(string nome)
    {
        // pasta "Temporada 1": "Episódio 02-03" não traz temporada no nome, ela vem da pasta
        var r = Rodar(Ep("Show/Temporada 1", "Show.S01E01.mkv"), Ep("Show/Temporada 1", nome), Ep("Show/Temporada 1", "Show.S01E04.mkv"));
        Assert.Empty(r.Faltando);   // sem o duplo cobrindo o 3, ele apareceria como buraco
    }

    [Fact]
    public void Temporada_completa_nao_tem_buraco() =>
        Assert.Empty(Rodar(Enumerable.Range(1, 12).Select(n => Ep("Show", $"Show.S01E{n:00}.mkv")).ToArray()).Faltando);

    // ─── repetidos ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Episodio_em_dois_arquivos_lista_os_dois_com_tamanho()
    {
        var r = Rodar(
            ("Series/Diarios de um Vampiro/Diarios.S08E13.1080p.mkv", 900 * MB),
            ("Series/Diarios de um Vampiro/Diarios.S08E13.720p.mkv", 400 * MB),
            ("Series/Diarios de um Vampiro/Diarios.S08E12.720p.mkv", 400 * MB));

        var rep = Assert.Single(r.Repetidos);

        Assert.Equal((8, 13), (rep.Temporada, rep.Episodio));
        Assert.Equal([900 * MB, 400 * MB], rep.Arquivos.Select(a => a.Bytes));
    }

    [Fact]
    public void Arquivo_duplo_junto_de_arquivo_avulso_do_mesmo_episodio_e_repetido()
    {
        var r = Rodar(Ep("Show", "Show.S01E01-02.mkv"), Ep("Show", "Show.S01E02.mkv"), Ep("Show", "Show.S01E03.mkv"));

        Assert.Equal([2], r.Repetidos.Select(x => x.Episodio));
    }

    // ─── série com 1 episódio / soltos ───────────────────────────────────────────────

    [Fact]
    public void Serie_com_um_unico_episodio()
    {
        var r = Rodar(Ep("Rick and Morty", "Rick.and.Morty.S09E01.mkv"),
                      Ep("Show", "Show.S01E01.mkv"), Ep("Show", "Show.S01E02.mkv"));

        var u = Assert.Single(r.SerieComUmEpisodio);
        Assert.Equal(("Rick and Morty", 9, 1), (u.Serie, u.Temporada, u.Episodio));
    }

    [Fact]
    public void Arquivo_solto_na_raiz_que_parece_episodio_mas_o_parser_nao_reconheceu()
    {
        var r = Rodar(
            ("Show - 05 - Titulo Qualquer.mkv", 300 * MB),
            ("Matrix.1999.1080p.BluRay.mkv", 2000 * MB),
            ("Series/Pasta/Show.S01E01.mkv", 300 * MB));

        Assert.Equal(["Show - 05 - Titulo Qualquer.mkv"], r.SoltosQueParecemEpisodio.Select(a => a.Caminho));
    }

    // ─── propaganda de uploader ──────────────────────────────────────────────────────

    [Fact]
    public void Video_pequeno_com_nome_de_uploader_e_suspeito_e_o_grande_nao()
    {
        var r = Rodar(
            ("Filmes/Matrix/COMANDOTORRENTS.mkv", 5 * MB),                   // propaganda
            ("Filmes/Matrix/BLUDV.com.mp4", 12 * MB),                        // propaganda
            ("Filmes/Matrix/Matrix.1999.COMANDOTORRENTS.mkv", 2000 * MB),    // o filme de verdade, com o nome do uploader
            ("Filmes/Alien/Alien.1979.mkv", 1500 * MB));

        Assert.Equal(["Filmes/Matrix/COMANDOTORRENTS.mkv", "Filmes/Matrix/BLUDV.com.mp4"],
            r.SuspeitosDePropaganda.Select(s => s.Arquivo.Caminho));
    }

    [Theory]
    [InlineData("1XBET")]
    [InlineData("LAPUMIA")]
    public void Outros_nomes_de_uploader(string marca) =>
        Assert.Single(Rodar(($"Filmes/X/{marca}.mp4", 3 * MB)).SuspeitosDePropaganda);

    [Fact]
    public void Sample_e_suspeito_de_qualquer_tamanho() =>
        Assert.Single(Rodar(("Filmes/Matrix/Matrix.1999.Sample.mkv", 200 * MB)).SuspeitosDePropaganda);

    [Fact]
    public void Palavra_parecida_com_sample_nao_e_sample() =>
        Assert.Empty(Rodar(("Filmes/Samples of Life/Samples.of.Life.1999.mkv", 900 * MB)).SuspeitosDePropaganda);

    // ─── utilitários ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { 5 }, "E05")]
    [InlineData(new[] { 5, 15, 33 }, "E05, E15, E33")]
    [InlineData(new[] { 14, 15, 16 }, "E14–E16")]
    [InlineData(new[] { 1, 2, 4, 6, 7 }, "E01–E02, E04, E06–E07")]
    public void Resumir_agrupa_sequencias(int[] numeros, string esperado) =>
        Assert.Equal(esperado, DiagnosticoCatalogo.Resumir(numeros));

    [Theory]
    [InlineData("Show.S07E14E15.mkv", 14, 15)]
    [InlineData("Show.S01E01-E02.mkv", 1, 2)]
    [InlineData("The.Twilight.Zone.[1x06-07].mkv", 6, 7)]
    [InlineData("Show.S01E05.mkv", 5, 5)]
    [InlineData("Show.S01E05 - 12 Monkeys.mkv", 5, 5)]       // "12" é o começo de um título, não uma faixa de 8 episódios
    [InlineData("Show.S01E05 - 2010.1080p.mkv", 5, 5)]
    public void Faixa_de_episodios_de_um_arquivo(string nome, int de, int ate)
    {
        var (_, d, a) = MediaNomeParser.FaixaDeEpisodios(nome, 1, de);
        Assert.Equal((de, ate), (d, a));
    }

    [Fact]
    public void Catalogo_vazio_nao_quebra()
    {
        var r = Rodar();
        Assert.Equal(0, r.TotalArquivos);
        Assert.Empty(r.Faltando);
        Assert.Empty(r.SuspeitosDePropaganda);
    }
}
