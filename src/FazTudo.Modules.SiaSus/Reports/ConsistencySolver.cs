using System.Globalization;
using FazTudo.Core.Models;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Models;

namespace FazTudo.Modules.SiaSus.Reports;

/// <summary>Onde o problema se resolve, quem resolve e para quem mandar.</summary>
/// <param name="Key">BPA, APAC, CNES, FPO ou SIA.</param>
/// <param name="Details">Para quem mandar (estabelecimentos) e, no caso da FPO, o que programar.</param>
/// <param name="IsAlternative">true = outra causa possível ("se o CBO estiver errado no CNES…").</param>
public sealed record ResolvedArea(string Key, string Label, string Who, string Action, IReadOnlyList<string> Details, bool IsAlternative);

/// <summary>Um tipo de ocorrência do relatório, explicado.</summary>
public sealed class ProblemExplanation
{
    public required string Code { get; init; }
    public required string Group { get; init; }
    public required string GroupMeaning { get; init; }
    public required string Description { get; init; }
    public required int Quantity { get; init; }
    /// <summary>Título em português claro (da base de erros) ou a descrição do SIA.</summary>
    public required string Title { get; init; }
    public DiagnosticErrorEntry? Knowledge { get; init; }
    /// <summary>"O que eu vi no seu relatório": conclusões tiradas das linhas + regras do SIGTAP.</summary>
    public required IReadOnlyList<string> Findings { get; init; }
    /// <summary>Passos de correção (da base de erros).</summary>
    public required IReadOnlyList<GuidedStep> Steps { get; init; }
    public required IReadOnlyList<OccurrenceRecord> Records { get; init; }
    /// <summary>Onde resolver: a primeira é a principal; as seguintes são causas alternativas.</summary>
    public required IReadOnlyList<ResolvedArea> Areas { get; init; }

    public ResolvedArea? MainArea => Areas.Count > 0 ? Areas[0] : null;
    public IReadOnlyList<ResolvedArea> AlternativeAreas => Areas.Skip(1).ToList();
    public string QuantityDisplay => Quantity == 1 ? "1 ocorrência" : $"{Quantity:N0} ocorrências";
    public bool IsBlocking => Group is "GRV" or "NOR";
}

public sealed class SolverResult
{
    public required ConsistencyReport Report { get; init; }
    public required IReadOnlyList<ProblemExplanation> Problems { get; init; }
    public required string Headline { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public int TotalOccurrences => Problems.Sum(p => p.Quantity);
    public int EstablishmentCount => Report.Records.Select(r => r.Cnes).Distinct().Count();

    /// <summary>Resposta em texto corrido, para colar em e-mail ou WhatsApp.</summary>
    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Relatório de consistência do SIA — {Report.Orgao} — competência {Report.Competencia}");
        sb.AppendLine(Headline);
        var n = 1;
        foreach (var p in Problems)
        {
            sb.AppendLine();
            sb.AppendLine($"{n++}. [{p.Code}] {p.Title} — {p.QuantityDisplay}");
            foreach (var area in p.Areas)
            {
                var prefix = area.IsAlternative ? "   → Também pode ser" : "   → Onde resolver";
                sb.AppendLine($"{prefix}: {area.Label}. Quem resolve: {area.Who} {area.Action}");
                foreach (var d in area.Details)
                {
                    sb.AppendLine($"      - {d}");
                }
            }

            foreach (var f in p.Findings)
            {
                sb.AppendLine($"   • {f}");
            }

            foreach (var s in p.Steps)
            {
                sb.AppendLine($"   {s.StepNumber}) {s.ActionTitle}: {s.DetailedInstruction}");
            }
        }

        sb.AppendLine();
        foreach (var note in Notes)
        {
            sb.AppendLine($"Obs.: {note}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Solucionador: lê o relatório de consistência do SIA e, para cada ocorrência, junta
/// o passo a passo da base de erros com o que dá para concluir das linhas e das regras do SIGTAP.
/// </summary>
public sealed class ConsistencySolver(SigtapRepository sigtap, IReadOnlyList<DiagnosticErrorEntry> knowledgeBase)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Código do SIA → erro da base de conhecimento (vem do shared/sia-knowledge.json).</summary>
    private static IReadOnlyDictionary<string, string> KnowledgeByCode => SiaErrorKnowledgeBase.SiaCodes;

    /// <summary>Procedimentos com faixa etária restrita e o código equivalente para fora da faixa.</summary>
    private static readonly Dictionary<string, (string Code, string When)> AgeAlternatives = new()
    {
        ["0203010086"] = ("0203010019", "se a paciente está fora da faixa de rastreamento e o exame tem indicação clínica (não é rastreamento)")
    };

    private static readonly Dictionary<string, int> GroupOrder = new() { ["GRV"] = 0, ["NOR"] = 1, ["AVS"] = 2, ["ACE"] = 3, ["ADV"] = 4 };

    public SolverResult Solve(ConsistencyReport report)
    {
        var problems = new List<ProblemExplanation>();
        var totals = report.Totals.Count > 0
            ? report.Totals
            : report.Records.GroupBy(r => (r.Group, r.Code, r.Description))
                .Select(g => new OccurrenceTotal(g.Key.Group, g.Key.Code, g.Count(), g.Key.Description)).ToList();

        foreach (var total in totals)
        {
            var records = report.Records.Where(r => r.Code == total.Code && r.Description == total.Description).ToList();
            var knowledge = KnowledgeByCode.TryGetValue(total.Code, out var id)
                ? knowledgeBase.FirstOrDefault(k => k.Id == id)
                : null;

            problems.Add(new ProblemExplanation
            {
                Code = total.Code,
                Group = total.Group,
                GroupMeaning = ConsistencyReport.GroupMeanings.GetValueOrDefault(total.Group, string.Empty),
                Description = total.Description,
                Quantity = total.Quantity,
                Title = knowledge?.Title ?? Capitalize(total.Description),
                Knowledge = knowledge,
                Findings = BuildFindings(total.Code, records, knowledge is null, total.Quantity),
                Steps = knowledge?.GuidedSteps ?? [],
                Records = records,
                Areas = ResolveAreas(knowledge, total, records)
            });
        }

        var ordered = problems
            .OrderBy(p => GroupOrder.GetValueOrDefault(p.Group, 9))
            .ThenByDescending(p => p.Quantity)
            .ToList();

        var blocking = ordered.Where(p => p.IsBlocking).Sum(p => p.Quantity);
        var totalCount = ordered.Sum(p => p.Quantity);
        var establishments = report.Records.Select(r => r.Cnes).Distinct().Count();
        var headline = totalCount == 0
            ? $"Recebi o relatório de {report.Competencia}: nenhuma ocorrência na consistência."
            : $"Recebi o relatório de {report.Competencia}: {totalCount:N0} ocorrência(s) de {ordered.Count} tipo(s) em {establishments} estabelecimento(s)." +
              (blocking == totalCount
                  ? " Todas impedem a aprovação até serem corrigidas."
                  : blocking > 0 ? $" {blocking:N0} impedem a aprovação; o resto é aviso." : " Nenhuma impede a aprovação.");

        var notes = new List<string>();
        if (!sigtap.IsOfficialTableLoaded)
        {
            notes.Add("Estou usando a base de exemplo do SIGTAP. Importe a tabela oficial da competência (Consultor SIGTAP) " +
                      "para eu dizer faixas de idade, quantidades máximas e CIDs aceitos de todos os procedimentos.");
        }

        notes.Add("Depois de corrigir na origem (prontuário, BPA-Mag, APAC-Mag, CNES ou FPO), reimporte e rode a consistência de novo.");

        return new SolverResult { Report = report, Problems = ordered, Headline = headline, Notes = notes };
    }

    private List<string> BuildFindings(string code, List<OccurrenceRecord> records, bool unknownCode, int quantity)
    {
        var findings = new List<string>();

        if (records.Count > 0 && records.Count < quantity)
        {
            findings.Add($"O quadro de totais soma {quantity} ocorrências, mas o detalhe só lista {records.Count} linha(s). " +
                         "Ao corrigir, confira o registro inteiro (todas as linhas/procedimentos), não só as linhas listadas.");
        }

        foreach (var byCnes in records.GroupBy(r => (r.Cnes, r.CnesName)))
        {
            var refs = byCnes.Select(DescribeReference).Distinct().ToList();
            findings.Add($"CNES {byCnes.Key.Cnes} ({byCnes.Key.CnesName}): {Join(refs, 6)}.");
        }

        switch (code)
        {
            case "FET2":
                foreach (var proc in records.Select(r => r.ProcedureCode).Distinct())
                {
                    var rule = sigtap.FindByCode(proc);
                    if (rule is null)
                    {
                        findings.Add($"O procedimento {proc} não está na tabela carregada; importe o SIGTAP oficial para eu dizer a faixa de idade.");
                        continue;
                    }

                    findings.Add($"{rule.FormattedCode} ({rule.Name}) só aceita pacientes de {rule.AgeRangeDisplay}. " +
                                 "Confira a data de nascimento nessas fichas: digitação errada é a causa mais comum.");
                    if (AgeAlternatives.TryGetValue(rule.Code, out var alt))
                    {
                        var altRule = sigtap.FindByCode(alt.Code);
                        var altName = altRule is null ? alt.Code : $"{altRule.FormattedCode} ({altRule.Name})";
                        findings.Add($"Se a idade estiver certa: {alt.When}, o código é {altName}.");
                    }
                }

                break;

            case "QTD2":
                foreach (var proc in records.Select(r => r.ProcedureCode).Distinct())
                {
                    var rule = sigtap.FindByCode(proc);
                    findings.Add(rule is { MaxQuantity: > 0 }
                        ? $"{rule.FormattedCode} ({rule.Name}): máximo de {rule.MaxQuantity} por registro no SIGTAP."
                        : $"Procedimento {proc}: importe o SIGTAP oficial para eu dizer a quantidade máxima.");
                }

                var oldCompetencias = records.Select(r => r.Competencia).Distinct().ToList();
                if (oldCompetencias.Count > 0)
                {
                    findings.Add($"Competência(s) dos registros: {string.Join(", ", oldCompetencias)}. " +
                                 "Se forem de meses anteriores, confira se não são sessões já apresentadas antes.");
                }

                break;

            case "9Q":
                foreach (var proc in records.Select(r => r.ProcedureCode).Distinct())
                {
                    var rule = sigtap.FindByCode(proc);
                    findings.Add(rule is { AllowedCids.Count: > 0 }
                        ? $"{rule.FormattedCode} ({rule.Name}) aceita {rule.AllowedCids.Count:N0} CID(s) como principal, por exemplo: {Join(rule.AllowedCids.ToList(), 8)}."
                        : $"Procedimento {proc}: importe o SIGTAP oficial para eu listar os CIDs aceitos.");
                }

                findings.Add("O CID é informação clínica: a troca precisa estar no laudo/prontuário, assinada pelo médico.");
                break;

            case "67":
            case "12":
                findings.AddRange(DescribeApacConflicts(records));
                break;

            case "Y8":
                foreach (var r in records)
                {
                    findings.Add($"APAC {r.Reference} é da competência {r.Competencia}. APACs do PMAE (OCI) valem só por 2 competências " +
                                 "(início e fim); fora disso o SIA não aceita.");
                }

                break;

            case "ERRo":
                if (records.Any(r => r.ProcedureCode.StartsWith("09", StringComparison.Ordinal)))
                {
                    findings.Add("São procedimentos do grupo 09 (OCI, Ofertas de Cuidados Integrados): a APAC precisa trazer as consultas e exames " +
                                 "que compõem a OCI como procedimentos secundários.");
                }

                break;

            case "ERRj":
                findings.Add("O CPF tem dígito verificador errado. Se o paciente tiver CNS válido, informar o CNS também resolve.");
                break;

            case "ERRI":
                findings.Add("Raça/cor precisa ser 01 a 05 (autodeclarada); 05 (indígena) exige também o código da etnia.");
                break;
        }

        if (unknownCode)
        {
            findings.Add("Ainda não tenho este código na base de erros. Procure a descrição no manual do SIA ou no BDNOTAS da versão.");
        }

        return findings;
    }

    // ---------------- Onde resolver ----------------

    private List<ResolvedArea> ResolveAreas(DiagnosticErrorEntry? knowledge, OccurrenceTotal total, List<OccurrenceRecord> records)
    {
        var keys = new List<string>();
        if (knowledge is not null)
        {
            keys.Add(knowledge.Area);
            if (knowledge.SecondaryArea is { } secondary)
            {
                keys.Add(secondary);
            }
        }
        else
        {
            keys.AddRange(InferAreas(total));
        }

        var result = new List<ResolvedArea>();
        foreach (var raw in keys)
        {
            var key = raw == "PRODUCAO" ? ProductionArea(records) : raw;
            if (result.Any(a => a.Key == key) || !SiaErrorKnowledgeBase.Areas.TryGetValue(key, out var info))
            {
                continue;
            }

            var details = key switch
            {
                "SIA" => [],
                "FPO" => SendTo(records, key).Concat(FpoAdvice(records)).ToList(),
                _ => SendTo(records, key)
            };
            result.Add(new ResolvedArea(key, info.Label, info.Who, info.Action, details, IsAlternative: result.Count > 0));
        }

        return result;
    }

    /// <summary>Para códigos que não estão na base: deduz pela descrição e pelo grupo.</summary>
    private static IEnumerable<string> InferAreas(OccurrenceTotal total)
    {
        var d = total.Description;
        if (d.Contains("FPO", StringComparison.Ordinal) || d.Contains("ORCAMENTO", StringComparison.Ordinal) || d.Contains("TETO", StringComparison.Ordinal))
        {
            return ["FPO"];
        }

        if (d.Contains("CNES", StringComparison.Ordinal) || d.Contains("HABILITA", StringComparison.Ordinal) ||
            d.Contains("SERVICO", StringComparison.Ordinal) || d.Contains("EQUIPAMENTO", StringComparison.Ordinal) ||
            d.Contains("ESTABELECIMENTO", StringComparison.Ordinal))
        {
            return ["CNES"];
        }

        // Legenda do próprio SIA: erro grave = "acerte na origem (FPO ou CNES)".
        return total.Group == "GRV" ? ["FPO", "CNES"] : ["PRODUCAO"];
    }

    private static string ProductionArea(List<OccurrenceRecord> records) =>
        records.Count(r => r.Instrument == "APAC") > records.Count / 2.0 ? "APAC" : "BPA";

    private static List<string> SendTo(List<OccurrenceRecord> records, string areaKey) =>
        records.GroupBy(r => (r.Cnes, r.CnesName))
            .Select(g =>
            {
                var what = areaKey switch
                {
                    "APAC" => Plural(g.Select(r => r.Reference).Distinct().Count(), "APAC", "APACs"),
                    "BPA" => Plural(g.Select(r => r.Reference).Distinct().Count(), "ficha", "fichas"),
                    _ => Plural(g.Count(), "ocorrência", "ocorrências")
                };
                return $"CNES {g.Key.Cnes} — {g.Key.CnesName} ({what})";
            })
            .ToList();

    /// <summary>
    /// O que programar na FPO: parte MAC ou não MAC (pelo financiamento do SIGTAP) e o valor (quantidade × valor unitário).
    /// </summary>
    private IEnumerable<string> FpoAdvice(List<OccurrenceRecord> records)
    {
        foreach (var group in records.GroupBy(r => (r.Cnes, r.ProcedureCode)))
        {
            var quantity = group.Count();
            var rule = sigtap.FindByCode(group.Key.ProcedureCode);
            if (rule is null)
            {
                yield return $"Procedimento {group.Key.ProcedureCode} (CNES {group.Key.Cnes}): importe o SIGTAP oficial para eu dizer se é MAC ou não MAC e o valor.";
                continue;
            }

            var isMac = rule.Financing.Contains("MAC", StringComparison.OrdinalIgnoreCase) &&
                        !rule.Financing.Contains("FAEC", StringComparison.OrdinalIgnoreCase);
            var section = isMac ? "na parte MAC" : $"na parte não MAC ({rule.Financing})";
            var value = rule.UnitValue > 0
                ? $" e valor de {(rule.UnitValue * quantity).ToString("C", PtBr)} ({quantity} × {rule.UnitValue.ToString("C", PtBr)} do SIGTAP)"
                : " (o valor unitário no SIGTAP é zero: confira se o procedimento é pago por incentivo antes de programar valor)";
            yield return $"FPO Magnético → CNES {group.Key.Cnes}, competência da produção: {section}, incluir {rule.FormattedCode} " +
                         $"({rule.Name}) com meta física de pelo menos {quantity}{value}.";
        }
    }

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    private IEnumerable<string> DescribeApacConflicts(List<OccurrenceRecord> records)
    {
        var seenPatients = new HashSet<string>();
        foreach (var record in records)
        {
            if (record.RelatedApacs.Count == 0)
            {
                continue;
            }

            var patient = record.RelatedApacs[0].PatientDocument;
            if (!seenPatients.Add(patient))
            {
                continue;
            }

            var apacs = record.RelatedApacs.DistinctBy(a => a.ApacNumber).OrderBy(a => a.Start).ToList();
            var parts = apacs.Select(a =>
            {
                var exit = a.ExitDate is { } d ? $", saída em {d:dd/MM/yyyy}" : string.Empty;
                var name = sigtap.FindByCode(a.ProcedureCode)?.Name;
                var proc = name is null ? a.ProcedureCode : $"{a.ProcedureCode} {name}";
                return $"APAC {a.ApacNumber} (proc. {proc}, {a.Start:dd/MM/yyyy} a {a.End:dd/MM/yyyy}{exit}, motivo {a.ExitReason})";
            });

            var text = $"Paciente {MaskDocument(patient)}: {string.Join(" e ", parts)}.";
            if (apacs.Count >= 2)
            {
                var a = apacs[0];
                var b = apacs[1];
                var endA = a.ExitDate ?? a.End;
                if (endA is { } ea && b.Start is { } sb && sb <= ea)
                {
                    text += $" Elas valem ao mesmo tempo de {sb:dd/MM/yyyy} até {ea:dd/MM/yyyy}.";
                }

                if (a.ProcedureCode != b.ProcedureCode)
                {
                    text += " Como o procedimento mudou, a anterior precisa estar encerrada (mudança de procedimento) antes do início da nova, " +
                            "e a nova entra como APAC inicial.";
                }
            }

            yield return text;
        }
    }

    private static string DescribeReference(OccurrenceRecord r) => r.Instrument switch
    {
        "APAC" => $"APAC {r.Reference} (proc. {r.ProcedureCode}, comp. {r.Competencia})",
        _ => $"folha/seq {r.Reference}" + (r.ThirdColumn.Length > 0 ? $" (prof. {MaskDocument(r.ThirdColumn)})" : string.Empty)
    };

    private static string MaskDocument(string doc) => doc.Length >= 8 ? $"{doc[..3]}…{doc[^4..]}" : doc;

    private static string Join(List<string> items, int max) =>
        items.Count <= max ? string.Join(", ", items) : string.Join(", ", items.Take(max)) + $" e mais {items.Count - max}";

    private static string Capitalize(string text)
    {
        var lower = text.ToLower(PtBr);
        return lower.Length == 0 ? lower : char.ToUpper(lower[0], PtBr) + lower[1..];
    }
}
