using FazTudo.Core.Models;
using FazTudo.Modules.SiaSus.Bpa;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Services;

namespace FazTudo.Tests;

public class BpaAnalyzerTests
{
    private const string ValidCns = "700000000000005";
    private const string Header = "01#BPA#202611000002000001";
    private const string Mamografia = "0204030188"; // base de exemplo: só F, 50-69 anos, CBO 225320, só BPA-I, R$ 45

    private static string BpaI(
        string proc = Mamografia, string cbo = "225320", string sex = "F",
        string birth = "19650510", string attendance = "20261105",
        string patientCns = ValidCns, string professionalCns = ValidCns,
        string race = "03", string ethnicity = "", int qty = 1, string cmp = "202611") =>
        "03" + "1234567" + cmp + professionalCns.PadRight(15) + cbo.PadRight(6) + attendance + "001" + "01"
        + proc.PadRight(10) + patientCns.PadRight(15) + sex + "355030" + "    " + "061"
        + qty.ToString().PadLeft(6, '0') + "01" + "".PadRight(13) + "BPA"
        + "MARIA DA SILVA".PadRight(30) + birth + race + ethnicity.PadRight(4) + "010";

    private static string BpaC(string proc, string cbo, int age = 30, int qty = 1) =>
        "02" + "1234567" + "202611" + cbo.PadRight(6) + "001" + "01" + proc.PadRight(10)
        + age.ToString().PadLeft(3, '0') + qty.ToString().PadLeft(6, '0') + "BPA";

    private static BatchAnalysisSummary Analyze(params string[] lines) =>
        new BpaAnalyzer(new SigtapRepository()).Analyze(BpaFile.Parse([Header, .. lines], "teste.txt"));

    [Fact]
    public void Linha_correta_nao_gera_critica()
    {
        var summary = Analyze(BpaI(qty: 2));

        Assert.Empty(summary.DetailedFindings);
        Assert.Equal(1, summary.TotalValidRecords);
        Assert.Equal(90m, summary.ApprovedEstimatedValue);
        Assert.Equal("11/2026", summary.ReferenceCompetencia);
    }

    [Theory]
    [InlineData("002", "sex")]
    [InlineData("004", "cbo")]
    [InlineData("005", "age")]
    [InlineData("CNS", "cns")]
    [InlineData("RAÇA", "race")]
    [InlineData("RAÇA", "ethnicity")]
    [InlineData("005", "birthAfter")]
    public void Detecta_cada_critica(string expectedCode, string scenario)
    {
        var line = scenario switch
        {
            "sex" => BpaI(sex: "M"),
            "cbo" => BpaI(cbo: "223505"),
            "age" => BpaI(birth: "19860101"), // 40 anos
            "cns" => BpaI(patientCns: "700000000000006"),
            "race" => BpaI(race: "99"),
            "ethnicity" => BpaI(race: "05", ethnicity: ""),
            "birthAfter" => BpaI(birth: "20261201"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var summary = Analyze(line);

        var finding = Assert.Single(summary.DetailedFindings);
        Assert.Equal(expectedCode, finding.ErrorCode);
        Assert.Equal(45m, summary.EstimatedGlosaValue);
        Assert.Equal(0, summary.TotalValidRecords);
    }

    [Fact]
    public void Procedimento_exclusivo_de_bpa_i_lancado_em_bpa_c()
    {
        var summary = Analyze(BpaC(Mamografia, "225320", age: 55));
        Assert.Equal("006/007", Assert.Single(summary.DetailedFindings).ErrorCode);
    }

    [Fact]
    public void Agrupa_por_causa_raiz_e_ordena_por_impacto()
    {
        var summary = Analyze(
            BpaI(cbo: "223505", qty: 3),
            BpaI(cbo: "223505", qty: 1),
            BpaI(race: "99"));

        Assert.Equal(2, summary.RootCauseGroups.Count);
        var top = summary.RootCauseGroups[0];
        Assert.Equal("004", top.ErrorCode);
        Assert.Equal(2, top.OccurrencesCount);
        Assert.Equal(4, top.TotalProceduresAffected);
        Assert.Equal(180m, top.TotalFinancialImpact);
        Assert.Equal("sia-bpa-004", top.LinkedKnowledgeErrorId);
        Assert.False(string.IsNullOrEmpty(top.RecommendedAction));
    }

    [Fact]
    public void Sem_tabela_oficial_procedimento_desconhecido_vira_aviso_e_nao_glosa()
    {
        var summary = Analyze(BpaI(proc: "0999999999"));

        Assert.Empty(summary.DetailedFindings);
        Assert.Contains(summary.Warnings, w => w.Contains("não verificados"));
    }

    [Fact]
    public void Avisa_sobre_competencia_diferente_e_linhas_desconhecidas()
    {
        var summary = Analyze(BpaI(cmp: "202610"), "99XXXXXXXX");

        Assert.Contains(summary.Warnings, w => w.Contains("competência diferente"));
        Assert.Contains(summary.Warnings, w => w.Contains("não reconhecida"));
    }

    [Fact]
    public void Parser_le_campos_do_bpa_i()
    {
        var record = Assert.Single(BpaFile.Parse([Header, BpaI(qty: 7)], "x").Records);

        Assert.Equal(BpaRecordType.Individual, record.Type);
        Assert.Equal("1234567", record.Cnes);
        Assert.Equal("225320", record.Cbo);
        Assert.Equal(Mamografia, record.ProcedureCode);
        Assert.Equal(ValidCns, record.PatientCns);
        Assert.Equal("F", record.Sex);
        Assert.Equal(7, record.Quantity);
        Assert.Equal(new DateOnly(1965, 5, 10), record.BirthDate);
        Assert.Equal(new DateOnly(2026, 11, 5), record.AttendanceDate);
        Assert.Equal("MARIA DA SILVA", record.PatientName);
        Assert.Equal("03", record.RaceColor);
    }
}
