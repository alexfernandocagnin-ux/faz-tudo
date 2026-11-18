using System.Reflection;
using FazTudo.Core.Abstractions;
using FazTudo.Core.Models;
using FazTudo.Core.Text;

namespace FazTudo.Core.Services;

/// <summary>
/// Catálogo dinâmico de módulos da plataforma.
/// Permite registrar módulos internos ou carregar DLLs de plugins externos da pasta ./plugins sem recompilar o Shell.
/// </summary>
public sealed class ModuleCatalog
{
    private readonly List<ISolutionModule> _modules = [];
    private readonly List<string> _loadErrors = [];

    public IReadOnlyList<ISolutionModule> Modules => _modules;

    /// <summary>Problemas encontrados ao carregar plugins (exibidos na Central de Módulos em vez de sumirem).</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    /// <returns>true se o módulo foi registrado; false se já existia um módulo com o mesmo Id.</returns>
    public bool RegisterModule(ISolutionModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (_modules.Any(m => string.Equals(m.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        module.Initialize();
        _modules.Add(module);
        return true;
    }

    public int DiscoverPluginsFromFolder(string pluginsDirectory)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            return 0;
        }

        var loadedCount = 0;
        foreach (var dllPath in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(dllPath);
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(dllPath);
            }
            catch (BadImageFormatException)
            {
                continue; // DLL nativa ou não-.NET: não é plugin, ignora em silêncio.
            }
            catch (Exception ex)
            {
                _loadErrors.Add($"{fileName}: falha ao carregar ({ex.Message})");
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (Exception ex)
            {
                _loadErrors.Add($"{fileName}: tipos não puderam ser lidos ({ex.Message})");
                continue;
            }

            foreach (var type in types.Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISolutionModule).IsAssignableFrom(t)))
            {
                try
                {
                    if (Activator.CreateInstance(type) is ISolutionModule instance && RegisterModule(instance))
                    {
                        loadedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _loadErrors.Add($"{fileName}: módulo {type.Name} falhou ao iniciar ({ex.GetBaseException().Message})");
                }
            }
        }

        return loadedCount;
    }

    /// <summary>Busca em todas as bases de conhecimento. Todos os termos precisam aparecer; acentos são ignorados.</summary>
    public IReadOnlyList<DiagnosticErrorEntry> SearchGlobalKnowledgeBase(string? query)
    {
        var allEntries = _modules.SelectMany(m => m.GetKnowledgeBase()).ToList();
        var terms = SearchText.Terms(query);
        if (terms.Length == 0)
        {
            return allEntries;
        }

        return allEntries
            .Where(entry =>
            {
                var haystack = SearchText.Normalize(string.Join(' ',
                    new[] { entry.ErrorCode, entry.Title, entry.Layer, entry.Symptom, entry.RootCause, entry.AffectedSystems }
                        .Concat(entry.SearchKeywords)));
                return terms.All(haystack.Contains);
            })
            .ToList();
    }
}
