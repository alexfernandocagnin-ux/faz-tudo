using FazTudo.Core.Models;

namespace FazTudo.Core.Abstractions;

public enum ModuleStage
{
    Active,
    Beta,
    Planned
}

/// <summary>
/// Contrato central da plataforma expansível ("Conserta SIA · CPD").
/// Qualquer nova área (SIA/SUS, SIHD/AIH, SCNES, TISS/TUSS, Fiscal/SPED, etc.)
/// implementa esta interface para se integrar automaticamente ao Shell, à busca global e à Central de Módulos.
/// </summary>
public interface ISolutionModule
{
    string Id { get; }
    string Name { get; }
    string Subtitle { get; }
    string DomainCategory { get; }
    string BadgeText { get; }
    string Version { get; }
    ModuleStage Stage { get; }
    string Description { get; }
    IReadOnlyList<string> Capabilities { get; }
    IReadOnlyList<string> PlannedRoadmapItems { get; }

    void Initialize();
    IReadOnlyList<DiagnosticErrorEntry> GetKnowledgeBase();
}
