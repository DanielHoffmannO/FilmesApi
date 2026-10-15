using System.Text;
using System.Text.RegularExpressions;

namespace FilmesApi.Services;

/// <summary>Glob de caminho relativo (<c>extra/Laranja.Mecanica*/1XBET*</c>, <c>**/Sample/*</c>), sem diferenciar
/// maiúscula. <c>*</c> casa qualquer coisa MENOS a barra; <c>?</c> um caractere (menos barra); <c>**</c> atravessa pastas, e
/// <c>**/</c> vale por "zero ou mais pastas" (então <c>**/Sample/*</c> pega <c>Sample/x.mkv</c> e <c>a/b/Sample/x.mkv</c>).
/// Todo o resto é literal — o ponto de <c>Laranja.Mecanica</c> é um ponto, não "qualquer coisa".</summary>
internal static class GlobCaminho
{
    public static Regex ParaRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var comBarra = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(comBarra ? "(?:.*/)?" : ".*");
                i += comBarra ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
