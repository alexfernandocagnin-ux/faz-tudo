using FazTudo.Core.Services;
using FazTudo.Modules.SiaSus;

namespace FazTudo.Desktop;

/// <summary>Serviços compartilhados entre as telas.</summary>
public sealed class AppServices(ModuleCatalog catalog, SiaSusModule sia)
{
    public ModuleCatalog Catalog { get; } = catalog;
    public SiaSusModule Sia { get; } = sia;

    /// <summary>Abre um erro na Base de Erros (usado pelos links do Triador e do Doutor TI).</summary>
    public event Action<string>? KnowledgeRequested;

    /// <summary>Disparado quando a tabela SIGTAP muda (ex.: importação da oficial).</summary>
    public event Action? SigtapChanged;

    public void ShowKnowledge(string? knowledgeId)
    {
        if (!string.IsNullOrEmpty(knowledgeId))
        {
            KnowledgeRequested?.Invoke(knowledgeId);
        }
    }

    public void NotifySigtapChanged() => SigtapChanged?.Invoke();
}
