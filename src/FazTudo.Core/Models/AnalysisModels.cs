namespace FazTudo.Core.Models;

public sealed class RejectedRecordDetail
{
    public required int LineNumber { get; init; }
    public required string Instrument { get; init; }
    public required string Cnes { get; init; }
    public required string Competencia { get; init; }
    public required string ProcedureCode { get; init; }
    public required string ProcedureName { get; init; }
    public required string Cbo { get; init; }
    public required string PatientOrProfessionalRef { get; init; }
    public required int Quantity { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorDescription { get; init; }
    public required decimal EstimatedLoss { get; init; }
    public required string LinkedKnowledgeErrorId { get; init; }
}

public sealed class RootCauseGroup
{
    public required string GroupKey { get; init; }
    public required string ErrorCode { get; init; }
    public required string LinkedKnowledgeErrorId { get; init; }
    public required string RootCauseTitle { get; init; }
    public required string RecommendedAction { get; init; }
    public required int OccurrencesCount { get; init; }
    public required int TotalProceduresAffected { get; init; }
    public required decimal TotalFinancialImpact { get; init; }
    public required IReadOnlyList<string> SampleDetails { get; init; }
}

public sealed class BatchAnalysisSummary
{
    public required string SourceName { get; init; }
    public required string DetectedFormat { get; init; }
    public required string ReferenceCompetencia { get; init; }
    public required int TotalRecordsEvaluated { get; init; }
    public required int TotalValidRecords { get; init; }
    public required int TotalRejectedOccurrences { get; init; }
    public required decimal EstimatedGlosaValue { get; init; }
    public required decimal ApprovedEstimatedValue { get; init; }
    public required IReadOnlyList<RootCauseGroup> RootCauseGroups { get; init; }
    public required IReadOnlyList<RejectedRecordDetail> DetailedFindings { get; init; }

    /// <summary>Avisos que não são rejeição (ex.: linhas ilegíveis, procedimentos não verificados por falta da tabela oficial).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
