using FilmesApi.Models;
using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary><see cref="FilaContinuar.Montar"/>: o topo do controle — o que ficou pela metade e o próximo episódio das séries vistas há pouco.</summary>
public class FilaContinuarTests
{
    private static readonly DateTime Agora = new(2026, 10, 27, 20, 0, 0, DateTimeKind.Utc);

    private static FilmeResponse Ep(int id, string serie, int temporada, int episodio, bool assistido = false) =>
        new(id, $"{serie} S{temporada}E{episodio}", null, null, assistido, Agora, null, null,
            EhEpisodio: true, Serie: serie, SerieChave: serie, Temporada: temporada, Episodio: episodio);

    private static FilmeResponse Filme(int id, bool assistido = false) =>
        new(id, $"Filme {id}", null, null, assistido, Agora, null, null);

    private static Dictionary<int, DateTime> Vistos(params (int Id, int DiasAtras)[] v) =>
        v.ToDictionary(x => x.Id, x => Agora.AddDays(-x.DiasAtras));

    [Fact]
    public void Pela_metade_vem_primeiro_e_depois_o_proximo_episodio()
    {
        var todos = new List<FilmeResponse> { Filme(1), Ep(10, "A", 1, 1, true), Ep(11, "A", 1, 2) };
        var fila = FilaContinuar.Montar(todos, [todos[0]], Vistos((10, 1)), Agora);

        Assert.Equal([(1, "continuar"), (11, "proximo")], fila.Select(i => (i.Filme.Id, i.Motivo)));
    }

    [Fact]
    public void Proximo_pula_o_que_ja_foi_visto_e_segue_a_ordem_temporada_episodio()
    {
        // assistiu o 1x2 por último; o 1x3 já estava visto → o próximo é o 2x1, mesmo com os ids fora de ordem
        var todos = new List<FilmeResponse>
        {
            Ep(20, "A", 2, 1), Ep(13, "A", 1, 3, true), Ep(12, "A", 1, 2, true), Ep(11, "A", 1, 1, true),
        };
        var fila = FilaContinuar.Montar(todos, [], Vistos((11, 5), (12, 1), (13, 6)), Agora);

        Assert.Equal(20, Assert.Single(fila).Filme.Id);
    }

    [Fact]
    public void Com_buraco_no_historico_o_proximo_e_o_mais_novo()
    {
        // viu 1, 2 e 5 (pulou 3 e 4): vai direto ao 8, o mais novo que ainda não viu
        var todos = Enumerable.Range(1, 8).Select(e => Ep(100 + e, "A", 1, e, assistido: e is 1 or 2 or 5)).ToList();
        var fila = FilaContinuar.Montar(todos, [], Vistos((101, 9), (102, 8), (105, 1)), Agora);

        Assert.Equal(108, Assert.Single(fila).Filme.Id);
    }

    [Fact]
    public void Buraco_sem_nada_mais_novo_depois_do_ultimo_nao_sugere_nada()
    {
        // pulou o 2 mas já viu o último que existe: não volta para o buraco por conta própria
        var todos = Enumerable.Range(1, 3).Select(e => Ep(100 + e, "A", 1, e, assistido: e is 1 or 3)).ToList();

        Assert.Empty(FilaContinuar.Montar(todos, [], Vistos((101, 5), (103, 1)), Agora));
    }

    [Fact]
    public void Serie_vista_fora_da_janela_ou_sem_data_nao_entra()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(10, "Velha", 1, 1, true), Ep(11, "Velha", 1, 2),
            Ep(20, "SemData", 1, 1, true), Ep(21, "SemData", 1, 2),
        };
        var fila = FilaContinuar.Montar(todos, [], Vistos((10, FilaContinuar.JanelaDias + 1)), Agora);

        Assert.Empty(fila);
    }

    [Fact]
    public void Serie_que_acabou_ou_com_episodio_pela_metade_nao_repete()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(10, "Fim", 1, 1, true),
            Ep(20, "Metade", 1, 1, true), Ep(21, "Metade", 1, 2), Ep(22, "Metade", 1, 3),
        };
        var fila = FilaContinuar.Montar(todos, [todos[2]], Vistos((10, 1), (20, 1)), Agora);

        Assert.Equal([(21, "continuar")], fila.Select(i => (i.Filme.Id, i.Motivo)));
    }

    [Fact]
    public void Series_ordenadas_pela_mais_recente_e_o_limite_vale()
    {
        var todos = new List<FilmeResponse>
        {
            Ep(10, "A", 1, 1, true), Ep(11, "A", 1, 2),
            Ep(20, "B", 1, 1, true), Ep(21, "B", 1, 2),
            Ep(30, "C", 1, 1, true), Ep(31, "C", 1, 2),
        };
        var vistos = Vistos((10, 5), (20, 1), (30, 3));

        Assert.Equal([21, 31, 11], FilaContinuar.Montar(todos, [], vistos, Agora).Select(i => i.Filme.Id));
        Assert.Equal([21, 31], FilaContinuar.Montar(todos, [], vistos, Agora, limite: 2).Select(i => i.Filme.Id));
    }
}
