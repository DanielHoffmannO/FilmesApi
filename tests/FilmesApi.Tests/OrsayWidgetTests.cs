using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FilmesApi.Services;

namespace FilmesApi.Tests;

public class OrsayWidgetTests
{
    private static string PastaComApp()
    {
        var dir = TestesComuns.CriarPastaTemp();
        File.WriteAllText(Path.Combine(dir, "config.xml"), "<widget/>");
        File.WriteAllText(Path.Combine(dir, "index.html"), "var S='" + OrsayWidget.Marcador + "';");
        Directory.CreateDirectory(Path.Combine(dir, "icon"));
        File.WriteAllBytes(Path.Combine(dir, "icon", "a.png"), [1, 2, 3]);
        return dir;
    }

    [Fact]
    public void Zip_tem_os_arquivos_na_raiz_e_o_servidor_no_index()
    {
        var dir = PastaComApp();
        try
        {
            var (zip, _) = OrsayWidget.Gerar(dir, "http://192.168.0.5");
            using var arquivo = new ZipArchive(new MemoryStream(zip));
            Assert.Equal(["config.xml", "icon/a.png", "index.html"], arquivo.Entries.Select(e => e.FullName).Order());

            using var leitor = new StreamReader(arquivo.GetEntry("index.html")!.Open(), Encoding.UTF8);
            var index = leitor.ReadToEnd();
            Assert.Contains("http://192.168.0.5", index);
            Assert.DoesNotContain(OrsayWidget.Marcador, index);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Tamanho_e_a_soma_dos_arquivos_ja_com_o_servidor_no_lugar()
    {
        var dir = PastaComApp();
        try
        {
            var (_, tamanho) = OrsayWidget.Gerar(dir, "http://x");
            // "<widget/>" (9) + 3 bytes do png + "var S='http://x';" (16)
            Assert.Equal(9 + 3 + "var S='http://x';".Length, tamanho);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Lista_xml_aponta_pro_zip_no_mesmo_servidor()
    {
        var xml = XDocument.Parse(OrsayWidget.ListaXml("http://192.168.0.5", 1234));
        var widget = xml.Root!.Element("list")!.Element("widget")!;
        Assert.Equal("ok", (string?)xml.Root.Attribute("stat"));
        Assert.Equal("http://192.168.0.5/widgets/Filmes.zip", (string?)widget.Element("download"));
        Assert.Equal("1234", (string?)widget.Element("compression")!.Attribute("size"));
        Assert.Equal("zip", (string?)widget.Element("compression")!.Attribute("type"));
    }
}
