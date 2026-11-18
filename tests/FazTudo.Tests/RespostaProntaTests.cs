using FazTudo.Modules.SiaSus;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Reports;

namespace FazTudo.Tests;

/// <summary>
/// Mesmos casos de web/tests/test_resposta_pronta.py: o caso real da OCI de mama (faltava a PAAF),
/// a lista por responsável e a tabela oficial padrão do projeto (shared/sigtap).
/// </summary>
public class RespostaProntaTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string SharedSigtapDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "shared", "sigtap")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "shared", "sigtap");
    }

    private static SiaSusModule OfficialModule()
    {
        var module = new SiaSusModule(Path.Combine(Path.GetTempPath(), "conserta-sia-tests-" + Guid.NewGuid().ToString("N")))
        {
            BundledTablesDirectory = SharedSigtapDir()
        };
        module.Initialize();
        Assert.True(module.Sigtap.IsOfficialTableLoaded);
        return module;
    }

    private static SolverResult Solve(bool withMirror)
    {
        var files = new List<string> { Path.Combine(Fixtures, "R0350-exemplo.OUT") };
        if (withMirror)
        {
            files.Add(Path.Combine(Fixtures, "R0330T-exemplo.OUT"));
        }

        return OfficialModule().SolveFiles(files);
    }

    [Fact]
    public void Abre_com_a_tabela_oficial_padrao_do_projeto()
    {
        var sigtap = OfficialModule().Sigtap;
        Assert.True(sigtap.AllProcedures.Count > 4000);
        Assert.Equal(6, sigtap.Competencia.Length);
        var oci = sigtap.FindByCode("090101009")!;
        Assert.StartsWith("OCI ", oci.Name);
        Assert.Equal(["0201010585", "0203010043"], oci.ObligatorySecondaries.Select(c => c.Code).Order());
        Assert.Contains(sigtap.DetailTexts(oci), d => d.Contains("2 competências"));
    }

    [Fact]
    public void Le_o_espelho_sem_dados_do_paciente()
    {
        var mirror = Assert.Single(ApacMirror.Load(Path.Combine(Fixtures, "R0330T-exemplo.OUT")));
        Assert.Equal("9424377", mirror.Cnes);
        Assert.Equal("3526702075237", mirror.Number);
        Assert.Equal("090101009", mirror.Principal);
        Assert.Equal(["0901010090", "0201010569", "0203010043", "0301010072"], mirror.Items.Select(i => i.ProcedureCode));
        Assert.True(mirror.Items[0].HasError);
        Assert.Equal("2082527", mirror.Items[1].ThirdPartyCnes);
    }

    [Fact]
    public void Com_espelho_diz_exatamente_o_que_falta()
    {
        var erro = Solve(withMirror: true).Problems.Single(p => p.Code == "ERRo");
        var text = erro.Actions.Single(a => a.Text.Contains("3526702075237")).Text;
        Assert.Contains("FALTA lançar o secundário obrigatório 02.01.01.058-5", text);
        Assert.Contains("Punção Aspirativa de Mama por Agulha Fina", text);
    }

    [Fact]
    public void Sem_espelho_lista_os_obrigatorios_e_pede_o_espelho()
    {
        var result = Solve(withMirror: false);
        var erro = result.Problems.Single(p => p.Code == "ERRo");
        Assert.All(erro.Actions, a => Assert.Contains("exige os secundários obrigatórios 02.01.01.058-5", a.Text));
        Assert.Contains(result.Notes, n => n.Contains("R0330T"));
    }

    [Fact]
    public void Lista_por_responsavel_com_mensagem_pronta()
    {
        var owners = Solve(withMirror: true).Owners;
        Assert.Equal([("BPA", "2074044"), ("APAC", "9424377"), ("APAC", "2082527")],
            owners.Select(o => (o.AreaKey, o.Cnes)).OrderBy(x => x.AreaKey == "BPA" ? 0 : 1).ThenByDescending(x => x.Cnes == "9424377"));
        var unidade = owners.Single(o => o.AreaKey == "BPA");
        Assert.Equal(["ERR8", "ERRI", "ERRj", "FET2"], unidade.Items.Select(i => i.Code).Order());
        Assert.StartsWith("Olá! Na consistência do SIA da competência 10/2026", unidade.Message);
        Assert.Contains("702/34", unidade.Message);
    }

    [Fact]
    public void Respostas_usam_a_tabela_oficial()
    {
        var acts = Solve(withMirror: true).Problems.SelectMany(p => p.Actions).GroupBy(a => a.Code)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(a => a.Text)));
        Assert.Contains("CIDs aceitos pelo SIGTAP: C53, D06, N87, N88", acts["9Q"]);
        Assert.Contains("o máximo permitido no SIGTAP é 1", acts["QTD2"]);
        Assert.Contains("validade fixa de 2 competências", acts["Y8"]);
        Assert.Contains("02.03.01.001-9", acts["FET2"]);
    }

    [Fact]
    public void So_o_espelho_sem_o_R0350_da_erro_claro()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            OfficialModule().SolveFiles([Path.Combine(Fixtures, "R0330T-exemplo.OUT")]));
        Assert.Contains("R0350", ex.Message);
    }

    [Theory]
    [InlineData("CONSULTA MEDICA EM ATENCAO ESPECIALIZADA", "Consulta Medica em Atencao Especializada")]
    [InlineData("OCI AVALIACAO DE CANCER DE MAMA", "OCI Avaliacao de Cancer de Mama")]
    public void Nomes_em_portugues_com_siglas(string raw, string expected) =>
        Assert.Equal(expected, SigtapRepository.TitlePt(raw));
}
