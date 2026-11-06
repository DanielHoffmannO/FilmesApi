using System.Numerics;

namespace FilmesApi.Services;

/// <summary>Trecho em segundos, nos dois episódios comparados.</summary>
public record TrechoComum(double InicioA, double FimA, double InicioB, double FimB)
{
    public double Duracao => FimA - InicioA;
}

/// <summary>
/// Acha a abertura comparando a impressão de áudio (chromaprint) do começo de dois episódios da mesma temporada:
/// a abertura é o trecho comprido que os dois têm igual. Mesma ideia do "Intro Skipper" do Jellyfin. Pura: só recebe
/// as duas sequências de inteiros (um a cada <see cref="SegundosPorItem"/>), então testa sem ffmpeg.
/// </summary>
public static class DetectorDeAbertura
{
    /// <summary>Duração de cada item do chromaprint "algorithm 2" (4096/3 amostras a 11025 Hz).</summary>
    public const double SegundosPorItem = 4096.0 / 3 / 11025;

    private const int LimiarDeBitsDiferentes = 6;   // de 32: mesmo som com ruído/dublagem diferente passa, outro som não
    private const int FalhasToleradas = 3;           // itens ruins seguidos que não quebram a corrida
    private const double DeslocamentoMaxSegundos = 180;   // abertura começa em pontos diferentes (com/sem "anteriormente")

    /// <summary>A melhor corrida em comum com pelo menos <paramref name="minimoSegundos"/>, ou null.</summary>
    public static TrechoComum? Encontrar(int[] a, int[] b, double minimoSegundos = 15)
    {
        var minimoItens = (int)Math.Ceiling(minimoSegundos / SegundosPorItem);
        var maxDesloc = (int)(DeslocamentoMaxSegundos / SegundosPorItem);
        (int Ini, int Fim, int Desloc)? melhor = null;   // Fim exclusivo, em índices de a

        for (var d = -maxDesloc; d <= maxDesloc; d++)   // b[i + d] ~ a[i]
        {
            int? ini = null; int ultimoBom = 0, falhas = 0;
            for (var i = Math.Max(0, -d); i < a.Length && i + d < b.Length; i++)
            {
                var casa = BitOperations.PopCount((uint)(a[i] ^ b[i + d])) <= LimiarDeBitsDiferentes;
                if (casa) { ini ??= i; ultimoBom = i; falhas = 0; }
                else if (ini is not null && ++falhas > FalhasToleradas)
                {
                    Considerar(a, ini.Value, ultimoBom + 1, d, minimoItens, ref melhor);
                    ini = null; falhas = 0;
                }
            }
            if (ini is not null) Considerar(a, ini.Value, ultimoBom + 1, d, minimoItens, ref melhor);
        }

        if (melhor is not { } m) return null;
        return new TrechoComum(m.Ini * SegundosPorItem, m.Fim * SegundosPorItem,
            (m.Ini + m.Desloc) * SegundosPorItem, (m.Fim + m.Desloc) * SegundosPorItem);
    }

    private static void Considerar(int[] a, int ini, int fim, int desloc, int minimoItens, ref (int Ini, int Fim, int Desloc)? melhor)
    {
        if (fim - ini < minimoItens) return;
        if (melhor is { } m && fim - ini <= m.Fim - m.Ini) return;
        // silêncio/tela preta dá o mesmo valor repetido nos dois episódios: não é abertura
        if (a.Skip(ini).Take(fim - ini).Distinct().Count() < (fim - ini) / 2) return;
        melhor = (ini, fim, desloc);
    }
}
