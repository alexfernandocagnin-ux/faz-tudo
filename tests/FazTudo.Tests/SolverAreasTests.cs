using FazTudo.Modules.SiaSus;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Reports;

namespace FazTudo.Tests;

/// <summary>"Onde resolver": BPA, APAC, CNES, FPO ou SIA, e para quem mandar.</summary>
public class SolverAreasTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "R0350-exemplo.OUT");

    private static SolverResult SolveFixture()
    {
        var module = new SiaSusModule(Path.Combine(Path.GetTempPath(), "faztudo-tests-" + Guid.NewGuid().ToString("N")));
        module.Initialize();
        return module.SolveConsistencyReport(FixturePath);
    }

    private static SolverResult SolveText(string text) =>
        new ConsistencySolver(new SigtapRepository(), SiaErrorKnowledgeBase.GetAll()).Solve(ConsistencyReport.Parse(text, "x.OUT"));

    [Theory]
    [InlineData("FET2", "BPA")]  // idade: ficha de BPA-I → a unidade corrige
    [InlineData("ERR8", "BPA")]
    [InlineData("ERRI", "BPA")]
    [InlineData("ERRj", "BPA")]
    [InlineData("67", "APAC")]
    [InlineData("12", "APAC")]
    [InlineData("ERRo", "APAC")]
    [InlineData("QTD2", "APAC")] // produção em APAC → prestador
    [InlineData("9Q", "APAC")]
    [InlineData("Y8", "APAC")]
    public void Cada_ocorrencia_do_relatorio_aponta_a_area_certa(string code, string area)
    {
        var problem = SolveFixture().Problems.Single(p => p.Code == code);
        Assert.Equal(area, problem.MainArea!.Key);
    }

    [Fact]
    public void Diz_para_qual_estabelecimento_mandar()
    {
        var fet2 = SolveFixture().Problems.Single(p => p.Code == "FET2");
        Assert.Contains("CNES 2074044 — IPC DR NICOLINO LIA NETO (2 fichas)", fet2.MainArea!.Details);

        var dup = SolveFixture().Problems.Single(p => p.Code == "67");
        Assert.Contains("CNES 2082527 — SANTA CASA DE ARARAQUARA (4 APACs)", dup.MainArea!.Details);
    }

    [Fact]
    public void Cbo_incompativel_aponta_bpa_e_cnes_como_alternativa()
    {
        var text = "RELATORIO DE OCORRENCIAS NA CONSISTENCIA - OUT/2026\n" +
                   "  NOR 004          1 PROCEDIMENTO NAO PERMITIDO PARA O CBO\n" +
                   " CNES : 1234567 UBS CENTRO\n" +
                   " TIPO COMPET. FL/SEQ         PROCED.    CBO    CNS PROFISS.  OCORRENCIA\n" +
                   " BPAI 10/2026         001/01 0301010072 223505 700000000000005 PROCEDIMENTO NAO PERMITIDO PARA O CBO\n";
        // "004" não está mapeado no relatório de consistência: deduz pela descrição → produção (BPA).
        var problem = SolveText(text).Problems.Single();
        Assert.Equal("BPA", problem.MainArea!.Key);
    }

    [Fact]
    public void Erro_de_fpo_diz_parte_mac_e_calcula_o_valor()
    {
        // 0301010072 na base de exemplo: MAC, R$ 10,00.
        var text = "RELATORIO DE OCORRENCIAS NA CONSISTENCIA - OUT/2026\n" +
                   "  GRV F07          3 PROCEDIMENTO SEM ORCAMENTO NA FPO\n" +
                   " CNES : 1234567 POLICLINICA CENTRAL\n" +
                   " TIPO COMPET. FL/SEQ         PROCED.    CBO    CNS PROFISS.  OCORRENCIA\n" +
                   " BPAI 10/2026         001/01 0301010072 225125 700000000000005 PROCEDIMENTO SEM ORCAMENTO NA FPO\n" +
                   " BPAI 10/2026         001/02 0301010072 225125 700000000000005 PROCEDIMENTO SEM ORCAMENTO NA FPO\n" +
                   " BPAI 10/2026         001/03 0301010072 225125 700000000000005 PROCEDIMENTO SEM ORCAMENTO NA FPO\n";

        var area = SolveText(text).Problems.Single().MainArea!;

        Assert.Equal("FPO", area.Key);
        Assert.Contains(area.Details, d =>
            d.Contains("na parte MAC") && d.Contains("03.01.01.007-2") && d.Contains("meta física de pelo menos 3") &&
            d.Contains("R$ 30,00"));
    }

    [Fact]
    public void Fpo_de_procedimento_faec_vai_na_parte_nao_mac()
    {
        // 0204030188 (mamografia de rastreamento) na base de exemplo: FAEC, R$ 45,00.
        var text = "RELATORIO DE OCORRENCIAS NA CONSISTENCIA - OUT/2026\n" +
                   "  GRV F07          1 PROCEDIMENTO SEM ORCAMENTO NA FPO\n" +
                   " CNES : 1234567 CLINICA DE IMAGEM\n" +
                   " TIPO COMPET. FL/SEQ         PROCED.    CBO    CNS PROFISS.  OCORRENCIA\n" +
                   " BPAI 10/2026         001/01 0204030188 225320 700000000000005 PROCEDIMENTO SEM ORCAMENTO NA FPO\n";

        var area = SolveText(text).Problems.Single().MainArea!;
        Assert.Contains(area.Details, d => d.Contains("na parte não MAC (FAEC)") && d.Contains("R$ 45,00"));
    }

    [Fact]
    public void Erro_grave_desconhecido_aponta_fpo_e_cnes_como_a_legenda_do_sia()
    {
        var text = "RELATORIO DE OCORRENCIAS NA CONSISTENCIA - OUT/2026\n" +
                   "  GRV ZZ1          1 ALGUMA COISA NOVA\n";
        var problem = SolveText(text).Problems.Single();

        Assert.Equal(["FPO", "CNES"], problem.Areas.Select(a => a.Key));
        Assert.True(problem.Areas[1].IsAlternative);
    }

    [Fact]
    public void Texto_para_copiar_traz_onde_resolver()
    {
        var text = SolveFixture().ToPlainText();
        Assert.Contains("→ Onde resolver: BPA — produção da unidade", text);
        Assert.Contains("→ Onde resolver: APAC — prestador", text);
    }
}
