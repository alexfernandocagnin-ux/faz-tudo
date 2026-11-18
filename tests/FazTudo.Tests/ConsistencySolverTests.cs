using FazTudo.Modules.SiaSus;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Reports;

namespace FazTudo.Tests;

/// <summary>Usa um R0350.OUT real do SIA (competência 10/2026) com os CNS/CPF trocados por números fictícios.</summary>
public class ConsistencySolverTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "R0350-exemplo.OUT");

    private static ConsistencyReport LoadReport() => ConsistencyReport.Load(FixturePath);

    private static SolverResult Solve()
    {
        var module = new SiaSusModule(Path.Combine(Path.GetTempPath(), "faztudo-tests-" + Guid.NewGuid().ToString("N")));
        module.Initialize();
        return module.SolveConsistencyReport(FixturePath);
    }

    [Fact]
    public void Le_o_cabecalho()
    {
        var report = LoadReport();

        Assert.Equal("SMS-ARARAQ", report.Orgao);
        Assert.Equal("10/2026", report.Competencia);
        Assert.Equal("BDSIA202610b", report.BdsiaVersion);
        Assert.Equal("17/11/2026 14:14:04", report.GeneratedAt);
    }

    [Fact]
    public void Le_o_quadro_de_totais()
    {
        var totals = LoadReport().Totals;

        Assert.Equal(10, totals.Count);
        Assert.Equal(28, totals.Sum(t => t.Quantity));
        Assert.All(totals, t => Assert.Equal("NOR", t.Group));
        Assert.Contains(totals, t => t is { Code: "ERRo", Quantity: 12 });
        Assert.Contains(totals, t => t is { Code: "FET2", Description: "IDADE INCOMPATIVEL COM PROCEDIMENTO" });
    }

    [Fact]
    public void Le_linhas_de_bpa_i()
    {
        var bpa = LoadReport().Records.Where(r => r.Instrument == "BPAI").ToList();

        Assert.Equal(6, bpa.Count);
        var first = bpa[0];
        Assert.Equal("2074044", first.Cnes);
        Assert.Equal("IPC DR NICOLINO LIA NETO", first.CnesName);
        Assert.Equal("10/2026", first.Competencia);
        Assert.Equal("701/97", first.Reference);
        Assert.Equal("0203010086", first.ProcedureCode);
        Assert.Equal("221105", first.Cbo);
        Assert.Equal(15, first.ThirdColumn.Length);
        Assert.Equal("FET2", first.Code);
    }

    [Fact]
    public void Le_linhas_de_apac_com_e_sem_cbo()
    {
        var apac = LoadReport().Records.Where(r => r.Instrument == "APAC").ToList();

        Assert.Equal(14, apac.Count);

        var withCbo = apac.First(r => r.Code == "QTD2");
        Assert.Equal("3526291001332", withCbo.Reference);
        Assert.Equal("0303050012", withCbo.ProcedureCode);
        Assert.Equal("225265", withCbo.Cbo);
        Assert.Equal("2003", withCbo.ThirdColumn);
        Assert.Equal("08/2026", withCbo.Competencia);

        var withoutCbo = apac.First(r => r.Code == "12");
        Assert.Equal("030402014", withoutCbo.ProcedureCode);
        Assert.Equal(string.Empty, withoutCbo.Cbo);
        Assert.Equal("1292", withoutCbo.ThirdColumn);
    }

    [Fact]
    public void Liga_a_tabela_de_apacs_relacionadas_as_ocorrencias_certas()
    {
        var records = LoadReport().Records.Where(r => r.Reference == "3526291013553").ToList();

        Assert.Equal(2, records.Count); // 12 e 67 na mesma APAC
        Assert.All(records, r => Assert.Equal(2, r.RelatedApacs.Count));
        var closed = records[0].RelatedApacs.Single(a => a.ApacNumber == "352629101355-3");
        Assert.Equal(new DateOnly(2026, 10, 1), closed.ExitDate);
        Assert.Equal("51", closed.ExitReason);
        var open = records[0].RelatedApacs.Single(a => a.ApacNumber == "352629103674-1");
        Assert.Null(open.ExitDate);
        Assert.Equal("21", open.ExitReason);
    }

    [Fact]
    public void Explica_cada_tipo_de_ocorrencia_com_passo_a_passo()
    {
        var result = Solve();

        Assert.Equal(10, result.Problems.Count);
        Assert.Equal(28, result.TotalOccurrences);
        Assert.Equal(3, result.EstablishmentCount);
        Assert.All(result.Problems, p =>
        {
            Assert.NotNull(p.Knowledge);
            Assert.NotEmpty(p.Steps);
            Assert.NotEmpty(p.Findings);
        });
        Assert.Equal("ERRo", result.Problems[0].Code); // maior quantidade primeiro
        Assert.Contains("Todas impedem a aprovação", result.Headline);
    }

    [Fact]
    public void Usa_o_sigtap_para_dizer_a_faixa_de_idade_e_o_codigo_alternativo()
    {
        var fet2 = Solve().Problems.Single(p => p.Code == "FET2");

        Assert.Contains(fet2.Findings, f => f.Contains("25a até 64a 11m"));
        Assert.Contains(fet2.Findings, f => f.Contains("02.03.01.001-9"));
        Assert.Contains(fet2.Findings, f => f.Contains("701/97") && f.Contains("715/72"));
    }

    [Fact]
    public void Descreve_a_sobreposicao_das_apacs_do_mesmo_paciente()
    {
        var dup = Solve().Problems.Single(p => p.Code == "67");

        Assert.Contains(dup.Findings, f =>
            f.Contains("352629101355-3") && f.Contains("352629103674-1") &&
            f.Contains("de 02/09/2026 até 01/10/2026") && f.Contains("mudança de procedimento"));
    }

    [Fact]
    public void Avisa_quando_o_detalhe_tem_menos_linhas_que_o_total()
    {
        var erro = Solve().Problems.Single(p => p.Code == "ERRo");
        Assert.Contains(erro.Findings, f => f.Contains("12 ocorrências") && f.Contains("4 linha(s)"));
    }

    [Fact]
    public void Resposta_em_texto_traz_todos_os_problemas()
    {
        var text = Solve().ToPlainText();

        Assert.Contains("SMS-ARARAQ", text);
        foreach (var code in new[] { "ERRo", "67", "12", "ERR8", "FET2", "QTD2", "9Q", "ERRI", "ERRj", "Y8" })
        {
            Assert.Contains($"[{code}]", text);
        }
    }

    [Fact]
    public void Arquivo_que_nao_e_o_relatorio_da_erro_claro()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ConsistencyReport.Parse("01#BPA#202611...", "remessa.txt"));
        Assert.Contains("R0350", ex.Message);
    }

    [Fact]
    public void Codigo_desconhecido_ainda_gera_resposta()
    {
        const string text = "RELATORIO DE OCORRENCIAS NA CONSISTENCIA - OUT/2026\n" +
                            "  GRV ZZ9          3 ALGUM ERRO NOVO DO SIA\n";
        var result = new ConsistencySolver(new SigtapRepository(), []).Solve(ConsistencyReport.Parse(text, "x.OUT"));

        var problem = Assert.Single(result.Problems);
        Assert.Equal("Algum erro novo do sia", problem.Title);
        Assert.Contains("impede o cálculo", problem.GroupMeaning);
        Assert.Contains(problem.Findings, f => f.Contains("Ainda não tenho este código"));
    }
}
