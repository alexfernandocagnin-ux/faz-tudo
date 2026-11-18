using System.IO.Compression;
using System.Text;
using FazTudo.Modules.SiaSus;
using FazTudo.Modules.SiaSus.Bpa;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Services;

namespace FazTudo.Tests;

public sealed class SigtapPersistenceAndCidTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("faztudo-test-").FullName;
    private string TablesDir => Path.Combine(_root, "TabelaUnificada_202611");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string ProcLine(string code, string name, string sex, int minAge, int maxAge, long vlSaCents) =>
        code.PadRight(10) + name.PadRight(250) + "M" + sex + "0001" + "0000" + "0000"
        + minAge.ToString().PadLeft(4, '0') + maxAge.ToString().PadLeft(4, '0')
        + "".PadLeft(12, '0') + vlSaCents.ToString().PadLeft(12, '0') + "".PadLeft(12, '0')
        + "06" + "000000" + "0000" + "202611";

    /// <summary>Fisioterapia (0302050027): BPA-I, CBO 223605, CIDs M545 e M796 aceitos como principal; S000 só secundário.</summary>
    private void WriteTables()
    {
        Directory.CreateDirectory(TablesDir);
        var l1 = Encoding.Latin1;
        File.WriteAllLines(Path.Combine(TablesDir, "tb_procedimento.txt"),
            [ProcLine("0302050027", "ATENDIMENTO FISIOTERAPEUTICO NAS ALTERACOES MOTORAS", "I", 0, 9999, 467)], l1);
        File.WriteAllLines(Path.Combine(TablesDir, "rl_procedimento_ocupacao.txt"), ["0302050027223605202611"], l1);
        File.WriteAllLines(Path.Combine(TablesDir, "rl_procedimento_registro.txt"), ["030205002702202611"], l1);
        File.WriteAllLines(Path.Combine(TablesDir, "rl_procedimento_cid.txt"),
            ["0302050027M545S202611", "0302050027M796S202611", "0302050027S000N202611"], l1);
    }

    private static string BpaI(string cid) =>
        "03" + "1234567" + "202611" + "700000000000005" + "223605" + "20261105" + "001" + "01"
        + "0302050027" + "700000000000005" + "F" + "355030" + cid.PadRight(4) + "045"
        + "000001" + "01" + "".PadRight(13) + "BPA" + "PACIENTE".PadRight(30) + "19810101" + "03" + "    " + "010";

    [Fact]
    public void Importa_cids_aceitos_como_principal()
    {
        WriteTables();
        var repo = new SigtapRepository();
        repo.ImportOfficialSigtap(TablesDir);

        var rule = repo.FindByCode("0302050027")!;
        Assert.Equal(["M545", "M796"], rule.AllowedCids);
        Assert.True(rule.RequiresCid);
    }

    [Theory]
    [InlineData("M545", null)]
    [InlineData("M796", null)]
    [InlineData("S000", "CID S000 incompatível")] // só secundário
    [InlineData("Z000", "CID Z000 incompatível")]
    [InlineData("", "CID ausente")]
    public void Triador_confere_cid_contra_o_sigtap(string cid, string? expectedGroup)
    {
        WriteTables();
        var repo = new SigtapRepository();
        repo.ImportOfficialSigtap(TablesDir);

        var summary = new BpaAnalyzer(repo).Analyze(BpaFile.Parse(["01#BPA#202611", BpaI(cid)], "x"));

        if (expectedGroup is null)
        {
            Assert.Empty(summary.DetailedFindings);
        }
        else
        {
            var group = Assert.Single(summary.RootCauseGroups);
            Assert.Equal("CID", group.ErrorCode);
            Assert.Contains(expectedGroup, group.GroupKey);
        }
    }

    [Fact]
    public void Importa_direto_do_zip_mesmo_com_subpasta()
    {
        WriteTables();
        var zip = Path.Combine(_root, "TabelaUnificada_202611_v2611.zip");
        ZipFile.CreateFromDirectory(TablesDir, zip, CompressionLevel.Fastest, includeBaseDirectory: true);

        var repo = new SigtapRepository();
        Assert.Equal(1, repo.ImportOfficialSigtap(zip));
        Assert.NotNull(repo.FindByCode("0302050027"));
    }

    [Fact]
    public void Zip_sem_tabela_da_erro_claro()
    {
        var empty = Path.Combine(_root, "vazio");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "leia.txt"), "nada");
        var zip = Path.Combine(_root, "errado.zip");
        ZipFile.CreateFromDirectory(empty, zip);

        var ex = Assert.Throws<InvalidDataException>(() => new SigtapRepository().ImportOfficialSigtap(zip));
        Assert.Contains("tb_procedimento.txt", ex.Message);
    }

    [Fact]
    public void Tabela_importada_continua_disponivel_na_proxima_execucao()
    {
        WriteTables();
        var dataDir = Path.Combine(_root, "dados");

        var first = new SiaSusModule(dataDir);
        first.Initialize();
        Assert.False(first.Sigtap.IsOfficialTableLoaded);
        first.ImportSigtap(TablesDir);

        var second = new SiaSusModule(dataDir); // "reabriu o programa"
        second.Initialize();

        Assert.True(second.Sigtap.IsOfficialTableLoaded);
        Assert.Equal(first.Sigtap.LoadedSourceLabel, second.Sigtap.LoadedSourceLabel);
        var rule = second.Sigtap.FindByCode("0302050027")!;
        Assert.Equal(4.67m, rule.UnitValue);
        Assert.Equal(["M545", "M796"], rule.AllowedCids);
        Assert.Equal(["223605"], rule.AllowedCbos);
    }

    [Fact]
    public void Cache_corrompido_e_ignorado()
    {
        var dataDir = Path.Combine(_root, "dados");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "sigtap-cache.json"), "{ isto não é json");

        var module = new SiaSusModule(dataDir);
        module.Initialize();

        Assert.False(module.Sigtap.IsOfficialTableLoaded);
        Assert.NotEmpty(module.Sigtap.AllProcedures); // base de exemplo
    }
}
