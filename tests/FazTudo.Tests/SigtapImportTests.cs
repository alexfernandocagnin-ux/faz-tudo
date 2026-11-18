using System.Text;
using FazTudo.Modules.SiaSus.Catalog;

namespace FazTudo.Tests;

public sealed class SigtapImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sigtap-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string ProcLine(string code, string name, string sex, int minAge, int maxAge, long vlSaCents, string fin, string comp) =>
        code.PadRight(10) + name.PadRight(250) + "M" + sex + "0001" + "0000" + "0000"
        + minAge.ToString().PadLeft(4, '0') + maxAge.ToString().PadLeft(4, '0')
        + "".PadLeft(12, '0') + vlSaCents.ToString().PadLeft(12, '0') + "".PadLeft(12, '0')
        + fin + "000000" + "0000" + comp;

    private void WriteTables(bool withLayout)
    {
        var latin1 = Encoding.Latin1;
        File.WriteAllLines(Path.Combine(_dir, "tb_procedimento.txt"),
        [
            ProcLine("0204030188", "MAMOGRAFIA BILATERAL PARA RASTREAMENTO", "F", 600, 839, 4500, "04", "202611"),
            ProcLine("0301010072", "CONSULTA MEDICA EM ATENÇÃO ESPECIALIZADA", "I", 0, 9999, 1000, "06", "202611"),
            "",
            "linha corrompida"
        ], latin1);
        File.WriteAllLines(Path.Combine(_dir, "rl_procedimento_ocupacao.txt"),
            ["0204030188225320202611", "0301010072225125202611", "0301010072225120202611"], latin1);
        File.WriteAllLines(Path.Combine(_dir, "rl_procedimento_registro.txt"),
            ["020403018802202611", "030101007202202611", "030101007201202611"], latin1);

        if (withLayout)
        {
            File.WriteAllLines(Path.Combine(_dir, "tb_procedimento_layout.txt"),
            [
                "Coluna,Tamanho,Inicio,Fim,Tipo",
                "CO_PROCEDIMENTO,10,1,10,VARCHAR2", "NO_PROCEDIMENTO,250,11,260,VARCHAR2", "TP_COMPLEXIDADE,1,261,261,VARCHAR2",
                "TP_SEXO,1,262,262,VARCHAR2", "QT_MAXIMA_EXECUCAO,4,263,266,NUMBER", "QT_DIAS_PERMANENCIA,4,267,270,NUMBER",
                "QT_PONTOS,4,271,274,NUMBER", "VL_IDADE_MINIMA,4,275,278,NUMBER", "VL_IDADE_MAXIMA,4,279,282,NUMBER",
                "VL_SH,12,283,294,NUMBER", "VL_SA,12,295,306,NUMBER", "VL_SP,12,307,318,NUMBER",
                "CO_FINANCIAMENTO,2,319,320,CHAR", "CO_RUBRICA,6,321,326,CHAR", "QT_TEMPO_PERMANENCIA,4,327,330,NUMBER",
                "DT_COMPETENCIA,6,331,336,CHAR"
            ], latin1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Importa_campos_nas_posicoes_corretas(bool withLayout)
    {
        WriteTables(withLayout);
        var repo = new SigtapRepository();

        var count = repo.ImportOfficialSigtapFolder(_dir);

        Assert.Equal(2, count);
        Assert.True(repo.IsOfficialTableLoaded);
        Assert.Contains("11/2026", repo.LoadedSourceLabel);

        var mamo = repo.FindByCode("02.04.03.018-8")!;
        Assert.Equal("F", mamo.SexRestriction);
        Assert.Equal(600, mamo.MinAgeMonths);
        Assert.Equal(839, mamo.MaxAgeMonths);
        Assert.Equal(45.00m, mamo.UnitValue);
        Assert.Equal("FAEC", mamo.Financing);
        Assert.Equal(["BPA-I"], mamo.AllowedInstruments);
        Assert.Equal(["225320"], mamo.AllowedCbos);

        var consulta = repo.FindByCode("0301010072")!;
        Assert.Equal("I", consulta.SexRestriction);
        Assert.Equal(9999, consulta.MaxAgeMonths);
        Assert.Equal(10.00m, consulta.UnitValue);
        Assert.Equal("MAC", consulta.Financing);
        Assert.Equal(["BPA-C", "BPA-I"], consulta.AllowedInstruments);
    }

    [Fact]
    public void Importacao_substitui_a_base_de_exemplo()
    {
        WriteTables(withLayout: false);
        var repo = new SigtapRepository();
        Assert.NotNull(repo.FindByCode("0211020036")); // ECG existe só na base de exemplo

        repo.ImportOfficialSigtapFolder(_dir);

        Assert.Null(repo.FindByCode("0211020036"));
        Assert.Equal(2, repo.AllProcedures.Count);
    }

    [Fact]
    public void Arquivo_sem_procedimentos_validos_nao_apaga_a_base_atual()
    {
        File.WriteAllLines(Path.Combine(_dir, "tb_procedimento.txt"), ["lixo", "mais lixo"]);
        var repo = new SigtapRepository();
        var before = repo.AllProcedures.Count;

        Assert.Throws<InvalidDataException>(() => repo.ImportOfficialSigtapFolder(_dir));
        Assert.Equal(before, repo.AllProcedures.Count);
        Assert.False(repo.IsOfficialTableLoaded);
    }

    [Fact]
    public void Pasta_sem_tb_procedimento_da_erro_claro() =>
        Assert.Throws<FileNotFoundException>(() => new SigtapRepository().ImportOfficialSigtapFolder(_dir));

    [Fact]
    public void Busca_ignora_acentos_e_aceita_codigo_formatado()
    {
        var repo = new SigtapRepository();
        Assert.Contains(repo.Search("pre-natal"), p => p.Code == "0301010110"); // "Pré-Natal"
        Assert.Single(repo.Search("02.04.03.018-8"));
    }
}
