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
    public Reports.SolverResult SolveConsistencyReport(string path) => SolveFiles([path]);

    /// <summary>
    /// Recebe os relatórios do SIA em qualquer ordem: o R0350 (ocorrências da consistência, obrigatório) e, se houver,
    /// o R0330T (espelho das APACs), que permite dizer exatamente qual secundário obrigatório faltou.
    /// </summary>
    public Reports.SolverResult SolveFiles(IEnumerable<string> paths)
    {
        Reports.ConsistencyReport? report = null;
        var mirrors = new List<Reports.ApacMirror>();
        foreach (var path in paths)
        {
            var text = File.ReadAllText(path, System.Text.Encoding.Latin1);
            if (Reports.ConsistencyReport.LooksLikeConsistencyReport(text))
            {
                report ??= Reports.ConsistencyReport.Parse(text, Path.GetFileName(path));
            }
            else if (Reports.ApacMirror.LooksLikeApacMirror(text))
            {
                mirrors.AddRange(Reports.ApacMirror.Parse(text));
            }
        }

        if (report is null)
        {
            throw new InvalidDataException(mirrors.Count > 0
                ? "Recebi o espelho das APACs (R0330T). Envie junto o R0350.OUT (ocorrências da consistência)."
                : "Nenhum dos arquivos é o Relatório de Ocorrências na Consistência do SIA (R0350.OUT).");
        }

        return new Reports.ConsistencySolver(Sigtap, _knowledgeBase).Solve(report, mirrors);
    }

    public EnvironmentDoctor Doctor { get; } = new();

    /// <summary>Pasta com a tabela SIGTAP padrão do projeto (copiada de shared/sigtap/ junto com o programa).</summary>
    public string BundledTablesDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "sigtap");

    public void Initialize()
    {
        _knowledgeBase = SiaErrorKnowledgeBase.GetAll();
        // 1º a tabela que alguém importou neste computador; senão, a padrão do projeto (a mesma do site).
        if (!Sigtap.TryLoadCache(SigtapCachePath))
        {
            LoadBundledTable();
        }
    }

    private void LoadBundledTable()
    {
        if (!Directory.Exists(BundledTablesDirectory))
        {
            return;
        }

        var competencia = string.Empty;
        var choice = Path.Combine(BundledTablesDirectory, "em-uso.json");
        if (File.Exists(choice))
        {
            try
            {
                competencia = System.Text.Json.JsonDocument.Parse(File.ReadAllText(choice))
                    .RootElement.GetProperty("competencia").GetString() ?? string.Empty;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                competencia = string.Empty;
            }
        }

        var file = competencia.Length == 6
            ? Path.Combine(BundledTablesDirectory, $"sigtap-{competencia}.json.gz")
            : Directory.EnumerateFiles(BundledTablesDirectory, "sigtap-*.json.gz").OrderDescending().FirstOrDefault();
        if (file is not null)
        {
            Sigtap.TryLoadSharedBundle(file);
        }
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
