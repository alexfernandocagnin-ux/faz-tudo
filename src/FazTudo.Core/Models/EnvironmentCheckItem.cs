namespace FazTudo.Core.Models;

public enum CheckHealthStatus
{
    Healthy,
    Attention,
    Critical,
    Info
}

public sealed class EnvironmentCheckItem
{
    public required string CheckId { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required CheckHealthStatus Status { get; init; }
    public required string DetectedState { get; init; }
    public required string Recommendation { get; init; }
    public string? LinkedKnowledgeErrorId { get; init; }

    public string StatusDisplay => Status switch
    {
        CheckHealthStatus.Healthy => "OK / Íntegro",
        CheckHealthStatus.Attention => "Atenção",
        CheckHealthStatus.Critical => "Crítico / Erro",
        _ => "Informativo"
    };
}
