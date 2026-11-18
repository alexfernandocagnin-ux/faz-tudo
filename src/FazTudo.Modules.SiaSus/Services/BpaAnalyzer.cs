using FazTudo.Core.Models;
using FazTudo.Modules.SiaSus.Bpa;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Models;

namespace FazTudo.Modules.SiaSus.Services;

/// <summary>
/// Triador BPA: aplica as críticas do SIGTAP (instrumento, CBO, sexo, idade, CID) e de cadastro
/// (CNS, raça/cor) a cada linha da remessa, antes de mandar para o SIA.
/// </summary>
public sealed class BpaAnalyzer(SigtapRepository sigtap)
{
    private static readonly Dictionary<string, DiagnosticErrorEntry> KnowledgeById =
        SiaErrorKnowledgeBase.GetAll().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

    private sealed record Finding(BpaRecord Record, SigtapProcedureRule? Rule, string Code, string KnowledgeId, string Description, string GroupDetail);

    public BatchAnalysisSummary Analyze(string path) => Analyze(BpaFile.Load(path));

    public BatchAnalysisSummary Analyze(BpaFile file)
    {
        var findings = new List<Finding>();
        var warnings = new List<string>(file.Warnings);
        var unknownProcedures = new HashSet<string>();
        var competenciaMismatch = 0;

        foreach (var record in file.Records)
        {
            if (!string.IsNullOrEmpty(file.HeaderCompetencia) && record.Competencia != file.HeaderCompetencia)
            {
                competenciaMismatch++;
            }

            var rule = sigtap.FindByCode(record.ProcedureCode);
            if (rule is null)
            {
                if (sigtap.IsOfficialTableLoaded)
                {
                    findings.Add(new Finding(record, null, "001", "sia-bpa-001",
                        $"Procedimento {record.ProcedureCode} não existe na tabela SIGTAP importada.", "procedimento inexistente"));
                }
                else
                {
                    unknownProcedures.Add(record.ProcedureCode);
                }
            }
            else
            {
                CheckRule(record, rule, findings);
            }

            if (record.Type == BpaRecordType.Individual)
            {
                CheckIndividualRegistration(record, rule, findings);
            }
        }

        if (unknownProcedures.Count > 0)
        {
            warnings.Add($"{unknownProcedures.Count:N0} procedimento(s) não verificados contra o SIGTAP porque a tabela oficial não foi importada " +
                         "(importe-a no Consultor SIGTAP para validar CBO, sexo, idade e instrumento de todos).");
        }

        if (competenciaMismatch > 0)
        {
            warnings.Add($"{competenciaMismatch:N0} linha(s) com competência diferente do cabeçalho ({FormatCompetencia(file.HeaderCompetencia)}).");
        }

        return BuildSummary(file, findings, warnings);
    }

    private static void CheckRule(BpaRecord record, SigtapProcedureRule rule, List<Finding> findings)
    {
        var knownInstruments = rule.AllowedInstruments.Any(i => i is "BPA-C" or "BPA-I" or "APAC" or "RAAS" or "AIH");
        if (knownInstruments && !rule.AllowedInstruments.Contains(record.InstrumentLabel))
        {
            findings.Add(new Finding(record, rule, "006/007", "sia-bpa-006",
                $"Procedimento aceita {rule.InstrumentsDisplay}, mas foi lançado em {record.InstrumentLabel}.",
                $"{record.InstrumentLabel} não permitido"));
        }

        if (!rule.AllowedCbos.Contains("*") && !rule.AllowedCbos.Contains(record.Cbo))
        {
            findings.Add(new Finding(record, rule, "004", "sia-bpa-004",
                $"CBO {Display(record.Cbo)} não é permitido para o procedimento (aceitos: {rule.CboSummary}).",
                $"CBO {Display(record.Cbo)}"));
        }

        if (record.Type == BpaRecordType.Individual)
        {
            if (rule.SexRestriction is "F" or "M" && record.Sex is "F" or "M" && record.Sex != rule.SexRestriction)
            {
                findings.Add(new Finding(record, rule, "002", "sia-bpa-002",
                    $"Procedimento exclusivo do sexo {rule.SexDisplay.ToLowerInvariant()}, paciente informado como '{record.Sex}'.",
                    "sexo incompatível"));
            }

            var cid = SigtapRepository.NormalizeCid(record.Cid);
            if (rule.RequiresCid && cid.Length == 0)
            {
                findings.Add(new Finding(record, rule, "CID", "sia-bpa-cid",
                    "Procedimento exige CID-10 e o campo veio vazio.", "CID ausente"));
            }
            else if (cid.Length > 0 && rule.AllowedCids.Count > 0 && !rule.AllowedCids.Contains(cid))
            {
                findings.Add(new Finding(record, rule, "CID", "sia-bpa-cid",
                    $"CID {cid} não é compatível com o procedimento no SIGTAP ({rule.AllowedCids.Count:N0} CIDs aceitos).",
                    $"CID {cid} incompatível"));
            }
        }

        var ageMonths = AgeInMonths(record);
        var hasAgeLimit = rule.MinAgeMonths > 0 || rule.MaxAgeMonths < 9999;
        if (hasAgeLimit && ageMonths is { } months && (months < rule.MinAgeMonths || months > rule.MaxAgeMonths))
        {
            findings.Add(new Finding(record, rule, "005", "sia-bpa-005",
                $"Idade {FormatAge(months)} fora da faixa do procedimento ({rule.AgeRangeDisplay}).",
                "idade fora da faixa"));
        }
    }

    private static void CheckIndividualRegistration(BpaRecord record, SigtapProcedureRule? rule, List<Finding> findings)
    {
        if (!string.IsNullOrEmpty(record.PatientCns) && !SusValidators.IsValidCns(record.PatientCns))
        {
            findings.Add(new Finding(record, rule, "CNS", "sia-bpa-cns",
                $"CNS do paciente {Mask(record.PatientCns)} reprova no dígito verificador (Módulo 11).", "CNS do paciente inválido"));
        }

        if (!string.IsNullOrEmpty(record.ProfessionalCns) && !SusValidators.IsValidCns(record.ProfessionalCns))
        {
            findings.Add(new Finding(record, rule, "CNS", "sia-bpa-cns",
                $"CNS do profissional {Mask(record.ProfessionalCns)} reprova no dígito verificador (Módulo 11).", "CNS do profissional inválido"));
        }

        if (record.BirthDate is { } birth && record.AttendanceDate is { } attendance && birth > attendance)
        {
            findings.Add(new Finding(record, rule, "005", "sia-bpa-005",
                $"Data de nascimento ({birth:dd/MM/yyyy}) posterior ao atendimento ({attendance:dd/MM/yyyy}).", "nascimento após atendimento"));
        }

        if (record.RaceColor == "99")
        {
            findings.Add(new Finding(record, rule, "RAÇA", "sia-bpa-raca",
                "Raça/cor '99 - Sem informação' não é aceita.", "raça/cor 99"));
        }
        else if (record.RaceColor == "05" && string.IsNullOrEmpty(record.Ethnicity))
        {
            findings.Add(new Finding(record, rule, "RAÇA", "sia-bpa-raca",
                "Raça/cor 05 (Indígena) sem código de etnia.", "etnia ausente"));
        }
    }

    private BatchAnalysisSummary BuildSummary(BpaFile file, List<Finding> findings, List<string> warnings)
    {
        decimal LineValue(BpaRecord r) => (sigtap.FindByCode(r.ProcedureCode)?.UnitValue ?? 0m) * r.Quantity;

        var rejectedLines = findings.Select(f => f.Record).Distinct().ToList();
        var validLines = file.Records.Except(rejectedLines).ToList();

        var details = findings
            .OrderBy(f => f.Record.LineNumber)
            .Select(f => new RejectedRecordDetail
            {
                LineNumber = f.Record.LineNumber,
                Instrument = f.Record.InstrumentLabel,
                Cnes = f.Record.Cnes,
                Competencia = FormatCompetencia(f.Record.Competencia),
                ProcedureCode = f.Record.ProcedureCode,
                ProcedureName = f.Rule?.Name ?? "(não encontrado)",
                Cbo = f.Record.Cbo,
                PatientOrProfessionalRef = Reference(f.Record),
                Quantity = f.Record.Quantity,
                ErrorCode = f.Code,
                ErrorDescription = f.Description,
                EstimatedLoss = LineValue(f.Record),
                LinkedKnowledgeErrorId = f.KnowledgeId
            })
            .ToList();

        var groups = findings
            .GroupBy(f => (f.KnowledgeId, f.Code, f.GroupDetail, f.Record.ProcedureCode))
            .Select(g =>
            {
                KnowledgeById.TryGetValue(g.Key.KnowledgeId, out var entry);
                var lines = g.Select(f => f.Record).Distinct().ToList();
                var procName = g.First().Rule?.Name ?? g.Key.ProcedureCode;
                return new RootCauseGroup
                {
                    GroupKey = $"{g.Key.Code} · {g.Key.GroupDetail} · {procName}",
                    ErrorCode = g.Key.Code,
                    LinkedKnowledgeErrorId = g.Key.KnowledgeId,
                    RootCauseTitle = entry?.Title ?? g.Key.GroupDetail,
                    RecommendedAction = entry?.GuidedSteps.FirstOrDefault()?.ActionTitle ?? string.Empty,
                    OccurrencesCount = g.Count(),
                    TotalProceduresAffected = lines.Sum(r => r.Quantity),
                    TotalFinancialImpact = lines.Sum(LineValue),
                    SampleDetails = g.Take(3).Select(f => $"Linha {f.Record.LineNumber}: {f.Description}").ToList()
                };
            })
            .OrderByDescending(g => g.TotalFinancialImpact)
            .ThenByDescending(g => g.OccurrencesCount)
            .ToList();

        return new BatchAnalysisSummary
        {
            SourceName = file.SourceName,
            DetectedFormat = file.HasHeader ? "Remessa BPA-Mag" : "Arquivo BPA sem cabeçalho",
            ReferenceCompetencia = FormatCompetencia(file.HeaderCompetencia.Length > 0
                ? file.HeaderCompetencia
                : file.Records.FirstOrDefault()?.Competencia ?? string.Empty),
            TotalRecordsEvaluated = file.Records.Count,
            TotalValidRecords = validLines.Count,
            TotalRejectedOccurrences = findings.Count,
            EstimatedGlosaValue = rejectedLines.Sum(LineValue),
            ApprovedEstimatedValue = validLines.Sum(LineValue),
            RootCauseGroups = groups,
            DetailedFindings = details,
            Warnings = warnings
        };
    }

    private static int? AgeInMonths(BpaRecord record)
    {
        if (record.BirthDate is { } birth && record.AttendanceDate is { } attendance && birth <= attendance)
        {
            var months = (attendance.Year - birth.Year) * 12 + attendance.Month - birth.Month;
            if (attendance.Day < birth.Day)
            {
                months--;
            }

            return months;
        }

        // BPA-C (ou BPA-I sem datas): só há a idade em anos. Usa o meio do ano para não reprovar quem está no limite.
        return record.AgeYears is { } years and < 150 ? years * 12 + 6 : null;
    }

    private static string FormatAge(int months) => months < 24 ? $"{months} meses" : $"{months / 12} anos";

    private static string FormatCompetencia(string aaaamm) =>
        aaaamm.Length == 6 ? $"{aaaamm[4..]}/{aaaamm[..4]}" : aaaamm;

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "(vazio)" : value;

    private static string Mask(string cns) => cns.Length >= 8 ? $"{cns[..3]}…{cns[^4..]}" : cns;

    private static string Reference(BpaRecord r)
    {
        var sheet = $"Folha {r.Folha}/{r.Sequencia}";
        if (r.Type == BpaRecordType.Consolidated)
        {
            return sheet;
        }

        var name = string.IsNullOrEmpty(r.PatientName) ? Mask(r.PatientCns) : r.PatientName;
        return $"{sheet} · {name}";
    }
}
