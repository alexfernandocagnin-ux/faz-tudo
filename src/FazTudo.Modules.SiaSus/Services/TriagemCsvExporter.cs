using System.Globalization;
using System.Text;
using FazTudo.Core.Models;

namespace FazTudo.Modules.SiaSus.Services;

/// <summary>
/// Exporta as linhas com crítica do Triador para CSV no formato que o Excel brasileiro abre direto
/// (separador ";", vírgula decimal, UTF-8 com BOM para manter os acentos).
/// </summary>
public static class TriagemCsvExporter
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static void Write(BatchAnalysisSummary summary, string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Write(summary, writer);
    }

    public static void Write(BatchAnalysisSummary summary, TextWriter writer)
    {
        writer.WriteLine(Row("Linha", "Crítica", "Instrumento", "CNES", "Competência", "Procedimento", "Nome do procedimento",
            "CBO", "Referência", "Quantidade", "O que está errado", "Valor estimado (R$)", "Como resolver"));

        var actionByKnowledgeId = summary.RootCauseGroups
            .GroupBy(g => g.LinkedKnowledgeErrorId)
            .ToDictionary(g => g.Key, g => g.First().RecommendedAction);

        foreach (var f in summary.DetailedFindings)
        {
            writer.WriteLine(Row(
                f.LineNumber.ToString(PtBr),
                f.ErrorCode,
                f.Instrument,
                f.Cnes,
                f.Competencia,
                f.ProcedureCode,
                f.ProcedureName,
                f.Cbo,
                f.PatientOrProfessionalRef,
                f.Quantity.ToString(PtBr),
                f.ErrorDescription,
                f.EstimatedLoss.ToString("N2", PtBr),
                actionByKnowledgeId.GetValueOrDefault(f.LinkedKnowledgeErrorId, string.Empty)));
        }
    }

    private static string Row(params string[] cells) => string.Join(';', cells.Select(Escape));

    private static string Escape(string value)
    {
        // Evita que o Excel interprete o texto como fórmula (e preserva zeros à esquerda de códigos).
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        if (value.Length > 1 && value[0] == '0' && value.All(char.IsDigit))
        {
            return $"=\"{value}\"";
        }

        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
