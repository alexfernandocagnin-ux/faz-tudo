using FazTudo.Core.Abstractions;
using FazTudo.Core.Models;
using FazTudo.Modules.SiaSus.Catalog;
using FazTudo.Modules.SiaSus.Services;

namespace FazTudo.Modules.SiaSus;

/// <summary>
/// Módulo SIA/SUS: base de erros de faturamento ambulatorial, Consultor SIGTAP, Triador BPA e Doutor TI DATASUS.
/// </summary>
public sealed class SiaSusModule : ISolutionModule
{
    private IReadOnlyList<DiagnosticErrorEntry> _knowledgeBase = [];

    /// <param name="dataDirectory">Onde guardar a tabela SIGTAP importada. Padrão: %LOCALAPPDATA%\FazTudo.</param>
    public SiaSusModule(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FazTudo");
    }

    public string Id => SiaErrorKnowledgeBase.ModuleKey;
    public string Name => "SIA/SUS";
    public string Subtitle => "Faturamento ambulatorial (BPA, APAC, RAAS)";
    public string DomainCategory => "Saúde Pública";
    public string BadgeText => "SIA";
    public string Version => "0.4.0";
    public ModuleStage Stage => ModuleStage.Beta;
    public string Description =>
        "Diagnóstico de glosas e rejeições do SIA/SUS: base de erros com passo a passo, consulta de regras SIGTAP, " +
        "triagem de remessas BPA antes do envio e checagem do ambiente Windows dos sistemas DATASUS.";

    public IReadOnlyList<string> Capabilities =>
    [
        "Solucionador: lê o relatório de consistência do SIA (R0350) e explica como resolver cada ocorrência",
        "Base de erros (SIGTAP, CNES, APAC/RAAS, FPO, TI/Transmissor)",
        "Consultor SIGTAP: importa o zip oficial e lembra dele entre sessões",
        "Triador BPA: CNS, CBO, sexo, idade, instrumento, CID, raça/cor — exporta para Excel",
        "Doutor TI DATASUS: VirtualStore, Firebird 3050, midas.dll, BDE"
    ];

    public IReadOnlyList<string> PlannedRoadmapItems =>
    [
        "Triagem de APAC e RAAS",
        "Ler outros relatórios do SIA (síntese de produção, APAC aprovada parcialmente)",
        "Validação contra o espelho do CNES (vínculos, serviços, equipamentos)"
    ];

    public string DataDirectory { get; }

    public string SigtapCachePath => Path.Combine(DataDirectory, "sigtap-cache.json");

    public SigtapRepository Sigtap { get; } = new();

    public BpaAnalyzer CreateBpaAnalyzer() => new(Sigtap);

    /// <summary>Lê o relatório de consistência do SIA (R0350.OUT) e explica cada ocorrência.</summary>
    public Reports.SolverResult SolveConsistencyReport(string path) =>
        new Reports.ConsistencySolver(Sigtap, _knowledgeBase).Solve(Reports.ConsistencyReport.Load(path));

    public EnvironmentDoctor Doctor { get; } = new();

    public void Initialize()
    {
        _knowledgeBase = SiaErrorKnowledgeBase.GetAll();
        Sigtap.TryLoadCache(SigtapCachePath);
    }

    public IReadOnlyList<DiagnosticErrorEntry> GetKnowledgeBase() => _knowledgeBase;

    /// <summary>Importa o SIGTAP (zip ou pasta) e salva para as próximas execuções.</summary>
    public int ImportSigtap(string zipOrFolderPath)
    {
        var count = Sigtap.ImportOfficialSigtap(zipOrFolderPath);
        Sigtap.SaveCache(SigtapCachePath);
        return count;
    }
}
