namespace FazTudo.Core.Models;

public enum ErrorSeverity
{
    CriticalGlosa,
    PartialGlosa,
    SystemBlocker,
    Warning
}

public sealed class GuidedStep
{
    public required int StepNumber { get; init; }
    public required string TargetSystem { get; init; }
    public required string ActionTitle { get; init; }
    public required string DetailedInstruction { get; init; }
    public string VerificationTip { get; init; } = string.Empty;
}

public sealed class DiagnosticErrorEntry
{
    public required string Id { get; init; }
    public required string ModuleId { get; init; }
    public required string ErrorCode { get; init; }
    public required string Title { get; init; }
    public required string Layer { get; init; }
    public required ErrorSeverity Severity { get; init; }
    public required string AffectedSystems { get; init; }
    public required string Symptom { get; init; }
    public required string RootCause { get; init; }
    public required string FinancialImpactNote { get; init; }
    public required IReadOnlyList<GuidedStep> GuidedSteps { get; init; }
    public IReadOnlyList<string> SearchKeywords { get; init; } = [];

    /// <summary>
    /// Onde o problema se resolve: PRODUCAO (BPA ou APAC, conforme o registro), BPA, APAC, CNES, FPO ou SIA.
    /// </summary>
    public string Area { get; init; } = "PRODUCAO";

    /// <summary>Área alternativa, quando a causa pode estar em outro lugar (ex.: CBO errado no CNES).</summary>
    public string? SecondaryArea { get; init; }

    public string SeverityDisplay => Severity switch
    {
        ErrorSeverity.CriticalGlosa => "Glosa Total / Rejeição",
        ErrorSeverity.PartialGlosa => "Retenção / Glosa Parcial",
        ErrorSeverity.SystemBlocker => "Travamento Técnico / Envio",
        _ => "Alerta Preventivo"
    };
}
