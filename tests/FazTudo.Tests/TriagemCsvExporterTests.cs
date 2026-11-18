using FazTudo.Modules.SiaSus.Bpa;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Services;

namespace FazTudo.Tests;

public class TriagemCsvExporterTests
{
    private static string BpaI(string cbo, int qty) =>
        "03" + "1234567" + "202611" + "700000000000005" + cbo + "20261105" + "001" + "01"
        + "0204030188" + "700000000000005" + "F" + "355030" + "    " + "061"
        + qty.ToString().PadLeft(6, '0') + "01" + "".PadRight(13) + "BPA" + "ANA; DA SILVA".PadRight(30) + "19650510" + "03" + "    " + "010";

    [Fact]
    public void Gera_csv_no_formato_do_excel_brasileiro()
    {
        var summary = new BpaAnalyzer(new SigtapRepository())
            .Analyze(BpaFile.Parse(["01#BPA#202611", BpaI("223505", 3)], "remessa.txt"));

        var writer = new StringWriter();
        TriagemCsvExporter.Write(summary, writer);
        var lines = writer.ToString().TrimEnd().Split(Environment.NewLine);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Linha;Crítica;Instrumento", lines[0]);
        Assert.Contains(";=\"004\";", lines[1]);          // Excel não vira "4"
        Assert.Contains("=\"0204030188\"", lines[1]);   // zeros à esquerda preservados
        Assert.Contains(";135,00;", lines[1]);          // 3 × R$ 45 com vírgula decimal
        Assert.Contains("\"Folha 001/01 · ANA; DA SILVA\"", lines[1]); // ";" dentro do texto vai entre aspas
        Assert.Contains("Listar os CBOs aceitos", lines[1]); // primeira ação recomendada
    }

    [Fact]
    public void Salva_em_utf8_com_bom_para_o_excel_reconhecer_acentos()
    {
        var summary = new BpaAnalyzer(new SigtapRepository())
            .Analyze(BpaFile.Parse(["01#BPA#202611", BpaI("223505", 1)], "remessa.txt"));
        var path = Path.GetTempFileName();
        try
        {
            TriagemCsvExporter.Write(summary, path);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
