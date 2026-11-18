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

/// <summary>Uma coisa a fazer, já respondida, para um responsável (área + estabelecimento).</summary>
public sealed record ActionItem(string AreaKey, string Cnes, string CnesName, string Code, string Text);

/// <summary>Tudo o que um responsável precisa fazer, com a mensagem pronta para mandar.</summary>
public sealed class OwnerGroup
{
    public required string AreaKey { get; init; }
    public required string AreaLabel { get; init; }
    public required string Who { get; init; }
    public required string Cnes { get; init; }
    public required string CnesName { get; init; }
    public List<ActionItem> Items { get; } = [];
    public string Message { get; set; } = string.Empty;

    public string Title => AreaKey switch
    {
        "BPA" => "Mandar para a unidade",
        "APAC" => "Mandar para o prestador",
        "CNES" => "Setor do CNES",
        "FPO" => "Programação (FPO Magnético)",
        "SIA" => "Aqui no SIA (CPD)",
        _ => AreaLabel
    };

    public string Where => Cnes.Length > 0 ? $"{CnesName} · CNES {Cnes}" : AreaLabel;
}

/// <summary>Um tipo de ocorrência do relatório, explicado.</summary>
public sealed class ProblemExplanation
{
    /// <summary>Respostas prontas (o que fazer), por estabelecimento.</summary>
    public IReadOnlyList<ActionItem> Actions { get; set; } = [];

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
    /// <summary>O que fazer, agrupado por responsável (estabelecimento + área).</summary>
    public IReadOnlyList<OwnerGroup> Owners { get; init; } = [];
    public int TotalOccurrences => Problems.Sum(p => p.Quantity);
    public int EstablishmentCount => Report.Records.Select(r => r.Cnes).Distinct().Count();

    /// <summary>Resposta em texto corrido, para colar em e-mail ou WhatsApp.</summary>
    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Relatório de consistência do SIA — {Report.Orgao} — competência {Report.Competencia}");
        sb.AppendLine(Headline);
        sb.AppendLine();
        sb.AppendLine("O QUE FAZER, POR RESPONSÁVEL");
        foreach (var g in Owners)
        {
            sb.AppendLine();
            sb.AppendLine($"▶ {g.AreaLabel} — CNES {g.Cnes} {g.CnesName}".TrimEnd());
            foreach (var i in g.Items)
            {
                sb.AppendLine($"   • [{i.Code}] {i.Text}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("DETALHES DE CADA OCORRÊNCIA");
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

    private Dictionary<string, ApacMirror> _mirrors = [];

    public SolverResult Solve(ConsistencyReport report, IReadOnlyList<ApacMirror>? mirrors = null)
    {
        _mirrors = (mirrors ?? []).GroupBy(m => m.Number).ToDictionary(g => g.Key, g => g.Last());
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

            var problem = new ProblemExplanation
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
            };
            problem.Actions = BuildActions(problem);
            problems.Add(problem);
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

        if (ordered.Any(p => p.Code == "ERRo") && _mirrors.Count == 0)
        {
            notes.Add("Dica: envie também o espelho das APACs (R0330T.OUT, \"Situação de Produção de APAC\") que eu digo " +
                      "exatamente qual procedimento obrigatório ficou faltando em cada APAC.");
        }

        notes.Add("Depois de corrigir na origem (prontuário, BPA-Mag, APAC-Mag, CNES ou FPO), reimporte e rode a consistência de novo.");

        return new SolverResult
        {
            Report = report, Problems = ordered, Headline = headline, Notes = notes, Owners = BuildOwners(ordered, report)
        };
    }

    // ---------------- Respostas prontas ----------------

    private string Proc(string code)
    {
        var rule = sigtap.FindByCode(code);
        return rule is null ? code : $"{rule.FormattedCode} {rule.Name}";
    }

    private List<ActionItem> BuildActions(ProblemExplanation p)
    {
        var area = p.MainArea?.Key ?? "BPA";
        var groups = p.Records.GroupBy(r => (r.Cnes, r.CnesName)).ToList();
        if (groups.Count == 0)
        {
            return [new ActionItem(area, string.Empty, string.Empty, p.Code, $"{p.Title}: {p.GroupMeaning}")];
        }

        return groups.SelectMany(g => Answer(p, g.ToList()).Select(t => new ActionItem(area, g.Key.Cnes, g.Key.CnesName, p.Code, t)))
            .ToList();
    }

    private IEnumerable<string> Answer(ProblemExplanation p, List<OccurrenceRecord> recs)
    {
        var refs = recs.Select(r => r.Reference).Distinct().ToList();
        var fichas = string.Join(", ", refs);
        var kind = recs.Count == 1 ? "Ficha" : "Fichas";

        switch (p.Code)
        {
            case "FET2":
                foreach (var proc in recs.Select(r => r.ProcedureCode).Distinct())
                {
                    var procRefs = string.Join(", ", recs.Where(r => r.ProcedureCode == proc).Select(r => r.Reference));
                    var rule = sigtap.FindByCode(proc);
                    if (rule is null)
                    {
                        yield return $"Fichas {procRefs}: idade do paciente fora da faixa do procedimento {proc}. Confira a data de nascimento.";
                        continue;
                    }

                    var text = $"Fichas {procRefs}: {rule.FormattedCode} ({rule.Name}) só aceita pacientes de {rule.AgeRangeDisplay}. " +
                               "Confira a data de nascimento no cadastro";
                    if (AgeAlternatives.TryGetValue(rule.Code, out var alt) && sigtap.FindByCode(alt.Code) is { } altRule)
                    {
                        text += $"; se estiver certa e o exame não for de rastreamento, lance como {altRule.FormattedCode} ({altRule.Name})";
                    }

                    yield return text + ".";
                }

                yield break;
            case "ERR8":
                yield return $"{kind} {fichas}: município de residência do paciente inválido. Corrigir o código IBGE do município " +
                             "(6 dígitos, ex.: 350320 = Araraquara) no cadastro do paciente e reenviar a remessa.";
                yield break;
            case "ERRI":
                yield return $"{kind} {fichas}: raça/cor do paciente inválida. Informar 01 Branca, 02 Preta, 03 Parda, 04 Amarela " +
                             "ou 05 Indígena (a 05 exige o código da etnia).";
                yield break;
            case "ERRj":
                yield return $"{kind} {fichas}: CPF do paciente com dígito verificador errado. Corrigir o CPF no cadastro " +
                             "(ou informar o CNS válido do paciente).";
                yield break;
            case "ERRo":
                foreach (var apac in refs)
                {
                    yield return AnswerSecondary(apac, recs.Where(r => r.Reference == apac).ToList());
                }

                yield break;
            case "QTD2":
                foreach (var r in recs)
                {
                    var rule = sigtap.FindByCode(r.ProcedureCode);
                    var limit = rule is { MaxQuantity: > 0 } ? $"o máximo permitido no SIGTAP é {rule.MaxQuantity}" : "passou do máximo";
                    yield return $"APAC {r.Reference} (comp. {r.Competencia}): {Proc(r.ProcedureCode)} — {limit}. " +
                                 "Corrigir a quantidade lançada (ou remover a linha repetida).";
                }

                yield break;
            case "9Q":
                foreach (var r in recs)
                {
                    var rule = sigtap.FindByCode(r.ProcedureCode);
                    var cids = rule is { AllowedCids.Count: > 0 } ? $" CIDs aceitos pelo SIGTAP: {Join(rule.AllowedCids.ToList(), 12)}." : string.Empty;
                    yield return $"APAC {r.Reference}: o CID informado não é aceito para {Proc(r.ProcedureCode)}.{cids} " +
                                 "Trocar pelo CID correto que estiver no laudo médico.";
                }

                yield break;
            case "Y8":
                foreach (var r in recs)
                {
                    var rule = sigtap.FindByCode(r.ProcedureCode);
                    var fixedTwo = rule is null || sigtap.DetailTexts(rule).Any(d => d.Contains("2 competências", StringComparison.Ordinal));
                    var ruleText = fixedTwo ? "o SIGTAP define validade fixa de 2 competências para esta APAC" : "a APAC tem validade de 2 competências";
                    yield return $"APAC {r.Reference} (comp. {r.Competencia}, {Proc(r.ProcedureCode)}): {ruleText} e ela foi apresentada " +
                                 "fora desse prazo. Não dá para cobrar nesta APAC; se o cuidado continua, é preciso emitir uma APAC nova.";
                }

                yield break;
            case "67":
            case "12":
                const string fix = "Encerrar a APAC anterior com motivo de saída por mudança de procedimento, na competência em que a nova " +
                                   "começou, e apresentar a nova como APAC inicial.";
                const string repeated = " Como o procedimento mudou, a anterior precisa estar encerrada (mudança de procedimento) antes do " +
                                        "início da nova, e a nova entra como APAC inicial.";
                var batches = p.Code == "12" ? recs.Select(r => new List<OccurrenceRecord> { r }).ToList() : [recs];
                foreach (var batch in batches)
                {
                    var prefix = p.Code == "12"
                        ? $"APAC {batch[0].Reference}: apresentada como continuidade, mas na competência anterior não houve permanência no mesmo procedimento. "
                        : string.Empty;
                    var conflicts = DescribeApacConflicts(batch).ToList();
                    foreach (var c in conflicts.Count > 0 ? conflicts : [string.Empty])
                    {
                        yield return $"{prefix}{c.Replace(repeated, string.Empty)} {fix}".Trim();
                    }
                }

                yield break;
            default:
                var first = p.Steps.Count > 0 ? p.Steps[0].ActionTitle : p.GroupMeaning;
                yield return $"{fichas}: {Capitalize(p.Description)}. {first}.";
                yield break;
        }
    }

    private string AnswerSecondary(string apac, List<OccurrenceRecord> recs)
    {
        var principal = recs.Count > 0 ? recs[0].ProcedureCode : string.Empty;
        var obligatory = sigtap.FindByCode(principal)?.ObligatorySecondaries.ToList() ?? [];
        var names = string.Join("; ", obligatory.Select(c => $"{Proc(c.Code)} (qtd. {c.Quantity})"));

        if (_mirrors.TryGetValue(apac, out var mirror) && obligatory.Count > 0)
        {
            var launched = mirror.Items.Select(i => i.ProcedureCode).ToHashSet();
            var missing = obligatory.Where(c => !launched.Contains(c.Code)).ToList();
            var done = string.Join("; ", mirror.Items.Where(i => !i.ProcedureCode.StartsWith(principal[..Math.Min(9, principal.Length)], StringComparison.Ordinal))
                .Select(i => Proc(i.ProcedureCode)));
            if (missing.Count > 0)
            {
                var miss = string.Join("; ", missing.Select(c => $"{Proc(c.Code)} (qtd. {c.Quantity})"));
                return $"APAC {apac} ({Proc(principal)}): FALTA lançar o secundário obrigatório {miss}. " +
                       $"Já lançados: {(done.Length > 0 ? done : "nenhum")}. Incluir na APAC e reapresentar (se não foi realizado, a OCI não está completa).";
            }

            return $"APAC {apac} ({Proc(principal)}): todos os obrigatórios ({names}) estão lançados no espelho — confira a quantidade e o CBO de cada um.";
        }

        if (obligatory.Count > 0)
        {
            return $"APAC {apac} ({Proc(principal)}): o SIGTAP exige os secundários obrigatórios {names}. " +
                   "Confira no espelho da APAC e inclua os que faltarem.";
        }

        return $"APAC {apac} ({Proc(principal)}): falta procedimento secundário obrigatório. " +
               "Confira as compatibilidades obrigatórias do procedimento no SIGTAP e inclua na APAC.";
    }

    private static List<OwnerGroup> BuildOwners(List<ProblemExplanation> problems, ConsistencyReport report)
    {
        var groups = new Dictionary<(string, string), OwnerGroup>();
        foreach (var a in problems.SelectMany(p => p.Actions))
        {
            if (!groups.TryGetValue((a.AreaKey, a.Cnes), out var g))
            {
                SiaErrorKnowledgeBase.Areas.TryGetValue(a.AreaKey, out var info);
                g = new OwnerGroup
                {
                    AreaKey = a.AreaKey, AreaLabel = info?.Label ?? a.AreaKey, Who = info?.Who ?? string.Empty, Cnes = a.Cnes, CnesName = a.CnesName
                };
                groups[(a.AreaKey, a.Cnes)] = g;
            }

            g.Items.Add(a);
        }

        var order = new Dictionary<string, int> { ["BPA"] = 0, ["APAC"] = 1, ["CNES"] = 2, ["FPO"] = 3, ["SIA"] = 4 };
        var result = groups.Values.OrderBy(g => order.GetValueOrDefault(g.AreaKey, 9)).ThenBy(g => g.CnesName).ToList();
        foreach (var g in result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Olá! Na consistência do SIA da competência {report.Competencia}, a produção do " +
                          $"{(g.CnesName.Length > 0 ? g.CnesName : "estabelecimento")} (CNES {g.Cnes}) teve estes problemas:");
            sb.AppendLine();
            var n = 1;
            foreach (var i in g.Items)
            {
                sb.AppendLine($"{n++}. {i.Text}");
            }

            sb.AppendLine();
            sb.Append("Por favor, corrijam no sistema de origem e reenviem a remessa. Qualquer dúvida, falem com o CPD.");
            g.Message = sb.ToString();
        }

        return result;
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
