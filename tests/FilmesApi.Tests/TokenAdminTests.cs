using FilmesApi.Services;
using Microsoft.AspNetCore.Http;

namespace FilmesApi.Tests;

/// <summary>Token opcional nas rotas que mexem no servidor. A TV e o celular NÃO têm onde guardar token, então o player
/// e o log de diagnóstico ficam de fora de propósito.</summary>
public class TokenAdminTests
{
    [Theory]
    [InlineData("POST", "/api/admin/recarregar-nomes", true)]
    [InlineData("GET", "/api/admin/qualquer-coisa", true)]          // /api/admin/* vale pra qualquer método
    [InlineData("POST", "/api/filmes/scan", true)]
    [InlineData("POST", "/API/FILMES/SCAN", true)]                  // maiúscula não escapa da proteção
    [InlineData("POST", "/api/filmes/scan/", true)]                 // nem a barra no fim
    [InlineData("POST", "/api/admin", true)]
    public void Rotas_protegidas(string metodo, string caminho, bool protegida) =>
        Assert.Equal(protegida, TokenAdmin.Protegida(metodo, new PathString(caminho)));

    [Theory]
    [InlineData("POST", "/api/diag/log")]                           // a TV manda sem token
    [InlineData("POST", "/api/player/play-pause")]                  // celular e TV
    [InlineData("POST", "/api/player/legenda-tv")]
    [InlineData("PUT", "/api/filmes/1/assistido")]
    [InlineData("GET", "/api/filmes/tela")]
    [InlineData("GET", "/api/filmes/diagnostico")]
    [InlineData("GET", "/api/status")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/api/filmes/scan")]                         // só o POST do scan escreve
    [InlineData("POST", "/api/filmes/scanner")]                     // prefixo de texto não é prefixo de rota
    [InlineData("POST", "/api/administrador")]
    public void Rotas_abertas(string metodo, string caminho) =>
        Assert.False(TokenAdmin.Protegida(metodo, new PathString(caminho)));

    [Theory]
    [InlineData(null, null, true)]            // sem token configurado, tudo passa como sempre
    [InlineData("", null, true)]
    [InlineData("", "qualquer", true)]
    [InlineData("segredo", "segredo", true)]
    [InlineData("segredo", "Segredo", false)]  // diferencia maiúscula
    [InlineData("segredo", "segredo ", false)] // espaço no fim não vale
    [InlineData("segredo", "segred", false)]
    [InlineData("segredo", "segredo-mais-longo", false)]
    [InlineData("segredo", "", false)]
    [InlineData("segredo", null, false)]       // header ausente
    public void Autorizacao(string? configurado, string? enviado, bool autorizado) =>
        Assert.Equal(autorizado, TokenAdmin.Autorizado(configurado, enviado));

    [Fact]
    public void Token_com_acento_e_comparado_pelos_bytes()
    {
        Assert.True(TokenAdmin.Autorizado("sênha-çerta", "sênha-çerta"));
        Assert.False(TokenAdmin.Autorizado("sênha-çerta", "senha-certa"));
    }
}
