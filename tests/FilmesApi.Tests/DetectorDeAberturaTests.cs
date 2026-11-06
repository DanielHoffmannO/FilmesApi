using FilmesApi.Services;

namespace FilmesApi.Tests;

public class DetectorDeAberturaTests
{
    private static int[] Aleatorio(int n, int semente)
    {
        var r = new Random(semente);
        return Enumerable.Range(0, n).Select(_ => r.Next(int.MinValue, int.MaxValue)).ToArray();
    }

    private static int Itens(double segundos) => (int)(segundos / DetectorDeAbertura.SegundosPorItem);

    [Fact]
    public void Acha_a_abertura_em_pontos_diferentes_de_cada_episodio()
    {
        var abertura = Aleatorio(Itens(60), 1);
        // A: 20 s de cena, abertura, cena. B: 70 s de cena, abertura, cena.
        var a = Aleatorio(Itens(20), 2).Concat(abertura).Concat(Aleatorio(Itens(300), 3)).ToArray();
        var b = Aleatorio(Itens(70), 4).Concat(abertura).Concat(Aleatorio(Itens(300), 5)).ToArray();

        var t = DetectorDeAbertura.Encontrar(a, b);

        Assert.NotNull(t);
        Assert.InRange(t!.InicioA, 19.5, 20.5);
        Assert.InRange(t.FimA, 79, 81);
        Assert.InRange(t.InicioB, 69.5, 70.5);
        Assert.InRange(t.FimB, 129, 131);
    }

    [Fact]
    public void Tolera_ruido_no_audio_dublagem_e_musica_por_cima()
    {
        var abertura = Aleatorio(Itens(45), 1);
        var ruidosa = abertura.Select((v, i) => i % 7 == 0 ? v ^ 0b1111 : v).ToArray();   // 4 bits trocados a cada 7 itens
        var a = Aleatorio(Itens(10), 2).Concat(abertura).Concat(Aleatorio(Itens(100), 3)).ToArray();
        var b = Aleatorio(Itens(10), 4).Concat(ruidosa).Concat(Aleatorio(Itens(100), 5)).ToArray();

        var t = DetectorDeAbertura.Encontrar(a, b);

        Assert.NotNull(t);
        Assert.InRange(t!.Duracao, 43, 46);
    }

    [Fact]
    public void Episodios_sem_nada_em_comum_nao_acham_abertura() =>
        Assert.Null(DetectorDeAbertura.Encontrar(Aleatorio(Itens(300), 1), Aleatorio(Itens(300), 2)));

    [Fact]
    public void Trecho_curto_demais_nao_conta()
    {
        var curto = Aleatorio(Itens(8), 1);
        var a = Aleatorio(Itens(50), 2).Concat(curto).Concat(Aleatorio(Itens(50), 3)).ToArray();
        var b = Aleatorio(Itens(30), 4).Concat(curto).Concat(Aleatorio(Itens(50), 5)).ToArray();

        Assert.Null(DetectorDeAbertura.Encontrar(a, b));
    }

    [Fact]
    public void Silencio_igual_nos_dois_nao_e_abertura() =>
        Assert.Null(DetectorDeAbertura.Encontrar(new int[Itens(120)], new int[Itens(120)]));
}
