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

    [Fact]
    public void Minimo_e_configuravel_trecho_de_7s_so_passa_com_minimo_menor()
    {
        var comum = Aleatorio(Itens(7), 1);
        var a = Aleatorio(Itens(50), 2).Concat(comum).Concat(Aleatorio(Itens(50), 3)).ToArray();
        var b = Aleatorio(Itens(30), 4).Concat(comum).Concat(Aleatorio(Itens(50), 5)).ToArray();

        Assert.Null(DetectorDeAbertura.Encontrar(a, b));   // padrão: 10 s
        Assert.Null(DetectorDeAbertura.Encontrar(a, b, minimoSegundos: 10));
        var t = DetectorDeAbertura.Encontrar(a, b, minimoSegundos: 4);   // mínimo de um pulo do Daniel
        Assert.NotNull(t);
        Assert.InRange(t!.Duracao, 6.5, 7.5);
    }

    [Fact]
    public void Criterio_folgado_estende_as_bordas_mas_nao_cria_trecho()
    {
        var firme = Aleatorio(Itens(30), 1);
        // 3 s de cada lado com 8 bits trocados por item: passa no folgado (<= 10), não no estrito (<= 6)
        static int[] Frouxo(int[] x) => x.Select(v => v ^ 0b11111111).ToArray();
        var borda = Aleatorio(Itens(3), 7);
        var a = Aleatorio(Itens(40), 2).Concat(borda).Concat(firme).Concat(borda).Concat(Aleatorio(Itens(40), 3)).ToArray();
        var b = Aleatorio(Itens(60), 4).Concat(Frouxo(borda)).Concat(firme).Concat(Frouxo(borda)).Concat(Aleatorio(Itens(40), 5)).ToArray();

        var t = DetectorDeAbertura.Encontrar(a, b);

        Assert.NotNull(t);
        Assert.InRange(t!.Duracao, 35.5, 36.5);   // 30 s firmes + 3 s + 3 s das bordas

        var soBorda = Aleatorio(Itens(30), 9);   // só "frouxo", sem miolo firme: o folgado nunca cria trecho
        Assert.Null(DetectorDeAbertura.Encontrar(soBorda, Frouxo(soBorda)));
    }
}
