using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>A regra pura que decide quais prefixos (pasta de primeiro nível) o scan pode limpar do catálogo quando o arquivo sumiu do disco:
/// protege contra montagem caída (pasta some em massa, ou existe mas vazia) sem deixar fantasma de pasta realmente apagada.</summary>
public class PrefixosRemoviveisTests
{
    private static HashSet<string> Vivos(params string[] p) => new(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Prefixo_com_arquivos_no_disco_e_sempre_removivel_arquivo_a_arquivo()
    {
        var r = FilmeService.PrefixosRemoviveis(["Filmes", "Series"], Vivos("Filmes", "Series"), 10, _ => false);

        Assert.Equal(["Filmes", "Series"], r.Order());
    }

    [Fact]
    public void Uma_pasta_inteira_apagada_de_proposito_sai_do_catalogo()
    {
        var r = FilmeService.PrefixosRemoviveis(["A", "B", "C"], Vivos("A", "B"), 10, _ => false);

        Assert.Contains("C", r);
    }

    [Fact]
    public void Pasta_que_existe_mas_esta_vazia_ou_inacessivel_parece_montagem_caida_e_fica()
    {
        var r = FilmeService.PrefixosRemoviveis(["A", "B", "C"], Vivos("A", "B"), 10, p => p == "C");

        Assert.DoesNotContain("C", r);
        Assert.Contains("A", r);
    }

    [Fact]
    public void Metade_ou_mais_dos_prefixos_sumindo_de_uma_vez_e_montagem_caida_e_nenhum_morto_e_removido()
    {
        // 2 de 4 mortos = 50%: o limite já conta como "em massa"
        var r = FilmeService.PrefixosRemoviveis(["A", "B", "C", "D"], Vivos("A", "B"), 10, _ => false);

        Assert.Equal(["A", "B"], r.Order());
    }

    [Fact]
    public void Menos_da_metade_sumindo_ainda_remove_so_os_mortos()
    {
        var r = FilmeService.PrefixosRemoviveis(["A", "B", "C", "D"], Vivos("A", "B", "C"), 10, _ => false);

        Assert.Equal(["A", "B", "C", "D"], r.Order());
    }

    [Fact]
    public void Raiz_morta_nunca_e_removida_porque_nao_ha_pasta_pra_conferir()
    {
        var r = FilmeService.PrefixosRemoviveis(["", "A", "B", "C"], Vivos("A", "B", "C"), 10, _ => false);

        Assert.DoesNotContain("", r);
    }

    [Fact]
    public void Comparacao_ignora_maiuscula_do_prefixo()
    {
        var r = FilmeService.PrefixosRemoviveis(["filmes"], Vivos("FILMES"), 5, _ => false);

        Assert.Contains("filmes", r);
    }
}
