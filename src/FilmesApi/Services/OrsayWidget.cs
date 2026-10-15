using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace FilmesApi.Services;

/// <summary>
/// App pra TVs Samsung antigas (plataforma "Orsay", antes do Tizen): o menu Develop da TV baixa
/// <c>/widgetlist.xml</c> do IP configurado e, por ele, o <c>.zip</c> do app. Os dois são montados
/// aqui na hora, a partir da pasta <c>wwwroot/orsay-widget</c>, com o endereço de onde a TV
/// pediu — assim não há IP gravado em arquivo nenhum. O app só abre o <c>tv.html</c> do servidor.
/// </summary>
public static class OrsayWidget
{
    internal const string Marcador = "__SERVIDOR__";
    public const string CaminhoZip = "/widgets/Filmes.zip";

    /// <summary>Zip com os arquivos de <paramref name="pasta"/> (na raiz do zip) e o tamanho somado
    /// deles, que o <c>widgetlist.xml</c> informa. No <c>index.html</c> o marcador vira
    /// <paramref name="servidorBaseUrl"/>.</summary>
    public static (byte[] Zip, long TamanhoArquivos) Gerar(string pasta, string servidorBaseUrl)
    {
        long total = 0;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var arquivo in Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var relativo = Path.GetRelativePath(pasta, arquivo).Replace('\\', '/');
                var bytes = File.ReadAllBytes(arquivo);
                if (relativo == "index.html")
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(Marcador, servidorBaseUrl));

                var entrada = zip.CreateEntry(relativo, CompressionLevel.Optimal);
                using var saida = entrada.Open();
                saida.Write(bytes);
                total += bytes.Length;
            }
        }
        return (ms.ToArray(), total);
    }

    public static string ListaXml(string servidorBaseUrl, long tamanhoArquivos)
    {
        var raiz = new XElement("rsp", new XAttribute("stat", "ok"),
            new XElement("list",
                new XElement("widget", new XAttribute("id", "FilmesApi"),
                    new XElement("title", "Filmes"),
                    new XElement("compression", new XAttribute("size", tamanhoArquivos), new XAttribute("type", "zip")),
                    new XElement("description", "Filmes e series do servidor de casa"),
                    new XElement("download", servidorBaseUrl + CaminhoZip))));
        // XDocument.ToString() omite a declaração; a TV segue o modelo oficial, que a traz.
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + raiz.ToString(SaveOptions.DisableFormatting);
    }
}
