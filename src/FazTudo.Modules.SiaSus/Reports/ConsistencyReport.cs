using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FazTudo.Modules.SiaSus.Reports;

/// <summary>Linha do quadro "TOTAIS DE OCORRENCIAS ENCONTRADAS".</summary>
public sealed record OccurrenceTotal(string Group, string Code, int Quantity, string Description);

/// <summary>Linha das tabelas de APACs relacionadas que o SIA imprime abaixo de algumas ocorrências (ex.: 12 e 67).</summary>
public sealed record RelatedApac(
    string PatientDocument,
    string ApacNumber,
    string ProcedureCode,
    DateOnly? Start,
    DateOnly? End,
    DateOnly? ExitDate,
    string ExitReason,
    string Cnes,
    string Competencia,
    string Movement);

/// <summary>Uma ocorrência apontada pelo SIA para um registro de produção.</summary>
public sealed class OccurrenceRecord
{
    public required string Cnes { get; init; }
    public required string CnesName { get; init; }
    /// <summary>BPAI, BPAC, APAC, RAAS…</summary>
    public required string Instrument { get; init; }
    public required string Competencia { get; init; }
    /// <summary>FL/SEQ (BPA) ou número da APAC.</summary>
    public required string Reference { get; init; }
    public required string ProcedureCode { get; init; }
    public required string Cbo { get; init; }
    /// <summary>CNS do profissional (BPA) ou número da remessa (APAC).</summary>
    public required string ThirdColumn { get; init; }
    public required string Description { get; init; }
    public required string Code { get; init; }
    public required string Group { get; init; }
    public List<RelatedApac> RelatedApacs { get; } = [];
    public int LineNumber { get; init; }
}

/// <summary>
/// Relatório R0350 do SIA — "RELATORIO DE OCORRENCIAS NA CONSISTENCIA".
/// Arquivo de impressão: tem quebras de página (\f), códigos de impressora (ESC…) e linhas terminadas em \r\r\n.
/// </summary>
public sealed class ConsistencyReport
{
    public required string SourceName { get; init; }
    public required string Orgao { get; init; }
    /// <summary>MM/AAAA da competência processada.</summary>
    public required string Competencia { get; init; }
    public required string BdsiaVersion { get; init; }
    public required string GeneratedAt { get; init; }
    public required IReadOnlyList<OccurrenceTotal> Totals { get; init; }
    public required IReadOnlyList<OccurrenceRecord> Records { get; init; }

    public static readonly IReadOnlyDictionary<string, string> GroupMeanings = new Dictionary<string, string>
    {
        ["GRV"] = "Erro grave: impede o cálculo do valor bruto. Acerte na origem (FPO ou CNES).",
        ["ACE"] = "Acerto: o SIA corrigiu algo na produção. Execute a consistência de novo.",
        ["AVS"] = "Aviso de correção na produção. Execute a consistência de novo.",
        ["NOR"] = "Problema na produção: não será aprovado enquanto o problema persistir.",
        ["ADV"] = "Advertência: vale verificar, mas não gera glosa."
    };

    private static readonly Regex EscapeSequence = new(@"\x1B[^A-Z@]*[A-Z@]", RegexOptions.Compiled);
    private static readonly Regex TotalLine = new(@"^\s*(GRV|ACE|AVS|NOR|ADV)\s+(\S+)\s+(\d+)\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex CnesLine = new(@"^\s*CNES\s*:\s*(\d{7})\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex RecordLine = new(@"^\s*(BPAI|BPAC|BPA|APAC|RAAS\S*)\s+(\d{2}/\d{4})\s+(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex TitleLine = new(@"RELATORIO DE OCORRENCIAS NA CONSISTENCIA\s*-\s*([A-Z]{3})/(\d{4})", RegexOptions.Compiled);
    private static readonly Regex DateTimeLine = new(@"^\s*(\d{2}/\d{2}/\d{4}).*?(\d{2}:\d{2}:\d{2})\s*$", RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"^\d+$", RegexOptions.Compiled);

    private static readonly string[] Months = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];

    public static ConsistencyReport Load(string path)
    {
        // Arquivos do SIA (Clipper/Delphi) vêm em página de código DOS; Latin1 lê os caracteres ASCII com segurança.
        var text = File.ReadAllText(path, Encoding.Latin1);
        return Parse(text, Path.GetFileName(path));
    }

    public static bool LooksLikeConsistencyReport(string text) =>
        text.Contains("OCORRENCIAS NA CONSISTENCIA", StringComparison.OrdinalIgnoreCase);

    public static ConsistencyReport Parse(string text, string sourceName)
    {
        if (!LooksLikeConsistencyReport(text))
        {
            throw new InvalidDataException(
                "Este arquivo não parece ser o Relatório de Ocorrências na Consistência do SIA (R0350). " +
                "No SIA, ele é gerado em Movimentação → Consistência e fica na pasta do processamento (ex.: R0350.OUT).");
        }

        var lines = EscapeSequence.Replace(text, string.Empty)
            .Replace("\f", "\n")
            .Replace("\r", string.Empty)
            .Split('\n');

        var orgao = string.Empty;
        var competencia = string.Empty;
        var version = string.Empty;
        var generatedAt = string.Empty;
        var totals = new List<OccurrenceTotal>();
        var records = new List<OccurrenceRecord>();

        var cnes = string.Empty;
        var cnesName = string.Empty;
        var inRelatedTable = false;
        var currentBlock = new List<OccurrenceRecord>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                inRelatedTable = false;
                currentBlock.Clear();
                continue;
            }

            if (version.Length == 0 && trimmed.StartsWith("BDSIA", StringComparison.Ordinal))
            {
                version = trimmed.Split(['*', ' '], StringSplitOptions.RemoveEmptyEntries)[0];
                continue;
            }

            if (orgao.Length == 0 && trimmed.Contains("SISTEMA DE INFORMACOES AMBULATORIAIS", StringComparison.Ordinal))
            {
                orgao = trimmed[..trimmed.IndexOf("SISTEMA DE INFORMACOES", StringComparison.Ordinal)].Trim();
                continue;
            }

            var title = TitleLine.Match(trimmed);
            if (title.Success)
            {
                if (competencia.Length == 0)
                {
                    var month = Array.IndexOf(Months, title.Groups[1].Value) + 1;
                    competencia = month > 0 ? $"{month:00}/{title.Groups[2].Value}" : $"{title.Groups[1].Value}/{title.Groups[2].Value}";
                    var dt = DateTimeLine.Match(trimmed);
                    generatedAt = dt.Success ? $"{dt.Groups[1].Value} {dt.Groups[2].Value}" : string.Empty;
                }

                continue;
            }

            var total = TotalLine.Match(line);
            if (total.Success && records.Count == 0 && cnes.Length == 0)
            {
                totals.Add(new OccurrenceTotal(total.Groups[1].Value, total.Groups[2].Value,
                    int.Parse(total.Groups[3].Value, CultureInfo.InvariantCulture), total.Groups[4].Value.Trim()));
                continue;
            }

            var cnesMatch = CnesLine.Match(line);
            if (cnesMatch.Success)
            {
                cnes = cnesMatch.Groups[1].Value;
                cnesName = cnesMatch.Groups[2].Value;
                continue;
            }

            if (trimmed.StartsWith("CNS/CPF", StringComparison.Ordinal))
            {
                inRelatedTable = true;
                continue;
            }

            if (inRelatedTable)
            {
                var related = ParseRelatedApac(trimmed);
                if (related is not null)
                {
                    foreach (var r in currentBlock)
                    {
                        r.RelatedApacs.Add(related);
                    }
                }

                continue;
            }

            var record = RecordLine.Match(line);
            if (record.Success && cnes.Length > 0)
            {
                var parsed = ParseRecord(record, totals, cnes, cnesName, i + 1);
                // Linhas seguidas do mesmo registro (mesma APAC/FL-SEQ) dividem a tabela de APACs relacionadas.
                if (currentBlock.Count > 0 && currentBlock[^1].Reference != parsed.Reference)
                {
                    currentBlock.Clear();
                }

                currentBlock.Add(parsed);
                records.Add(parsed);
            }
        }

        return new ConsistencyReport
        {
            SourceName = sourceName,
            Orgao = orgao,
            Competencia = competencia,
            BdsiaVersion = version,
            GeneratedAt = generatedAt,
            Totals = totals,
            Records = records
        };
    }

    private static OccurrenceRecord ParseRecord(Match match, List<OccurrenceTotal> totals, string cnes, string cnesName, int lineNumber)
    {
        var rest = match.Groups[3].Value;

        // A descrição é o texto que casa com um item do quadro de totais (é de lá que vem o código do erro).
        var total = totals
            .Where(t => rest.EndsWith(t.Description, StringComparison.Ordinal) ||
                        (t.Description.Length > 20 && rest.Contains(t.Description[..20], StringComparison.Ordinal)))
            .OrderByDescending(t => t.Description.Length)
            .FirstOrDefault();

        string prefix;
        string description;
        if (total is not null)
        {
            var at = rest.LastIndexOf(total.Description, StringComparison.Ordinal);
            if (at < 0)
            {
                at = rest.IndexOf(total.Description[..20], StringComparison.Ordinal);
            }

            prefix = rest[..at];
            description = total.Description;
        }
        else
        {
            // Sem casar com o quadro: a descrição começa no primeiro token que não é número.
            var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var firstText = Array.FindIndex(tokens, t => !Digits.IsMatch(t.Replace("/", string.Empty).Replace("-", string.Empty)));
            prefix = firstText < 0 ? rest : string.Join(' ', tokens[..firstText]);
            description = firstText < 0 ? string.Empty : string.Join(' ', tokens[firstText..]);
        }

        var parts = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var reference = parts.ElementAtOrDefault(0) ?? string.Empty;
        var procedure = parts.ElementAtOrDefault(1) ?? string.Empty;
        var cbo = string.Empty;
        var third = string.Empty;
        var remaining = parts.Skip(2).ToList();
        if (remaining.Count >= 2)
        {
            cbo = remaining[0];
            third = remaining[1];
        }
        else if (remaining.Count == 1)
        {
            if (remaining[0].Length == 6)
            {
                cbo = remaining[0];
            }
            else
            {
                third = remaining[0];
            }
        }

        return new OccurrenceRecord
        {
            Cnes = cnes,
            CnesName = cnesName,
            Instrument = match.Groups[1].Value,
            Competencia = match.Groups[2].Value,
            Reference = reference,
            ProcedureCode = procedure,
            Cbo = cbo,
            ThirdColumn = third,
            Description = description,
            Code = total?.Code ?? string.Empty,
            Group = total?.Group ?? string.Empty,
            LineNumber = lineNumber
        };
    }

    private static RelatedApac? ParseRelatedApac(string line)
    {
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // CNS/CPF NUM_APAC PROCED. INIC FIM [OCOR] MOT UPS CMP MVM
        if (t.Length is not (9 or 10) || !Digits.IsMatch(t[0]))
        {
            return null;
        }

        var hasExitDate = t.Length == 10;
        var offset = hasExitDate ? 1 : 0;
        return new RelatedApac(
            PatientDocument: t[0],
            ApacNumber: t[1],
            ProcedureCode: t[2],
            Start: ParseDate(t[3]),
            End: ParseDate(t[4]),
            ExitDate: hasExitDate ? ParseDate(t[5]) : null,
            ExitReason: t[5 + offset],
            Cnes: t[6 + offset],
            Competencia: t[7 + offset],
            Movement: t[8 + offset]);
    }

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
