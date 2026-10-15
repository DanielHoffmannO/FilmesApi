using System.Security.Cryptography;
using System.Text;

namespace FilmesApi.Services;

/// <summary>
/// Token opcional pras rotas que mexem no servidor (<c>/api/admin/*</c> e o scan manual <c>POST /api/filmes/scan</c>), que
/// ficam abertas na rede local. <c>AdminToken</c> definido → exige o header <c>X-Admin-Token</c>; vazio → como sempre foi.
/// NÃO vale pras rotas do player nem pro <c>/api/diag/log</c>: a TV e o celular não têm onde guardar um token.
/// </summary>
internal static class TokenAdmin
{
    public const string Header = "X-Admin-Token";

    /// <summary>Esta requisição é uma das protegidas?</summary>
    public static bool Protegida(string metodo, PathString caminho) =>
        caminho.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase)
        || (HttpMethods.IsPost(metodo) && caminho.StartsWithSegments("/api/filmes/scan", StringComparison.OrdinalIgnoreCase));

    /// <summary>Sem token configurado (vazio) passa tudo. Com token, o header tem que bater EXATAMENTE — comparação em tempo
    /// constante, pra o tempo de resposta não ir revelando o token caractere a caractere.</summary>
    public static bool Autorizado(string? configurado, string? enviado)
    {
        if (string.IsNullOrEmpty(configurado)) return true;
        if (enviado is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configurado), Encoding.UTF8.GetBytes(enviado));
    }
}
