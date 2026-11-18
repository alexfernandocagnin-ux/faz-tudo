using FazTudo.Core.Services;
using FazTudo.Modules.SiaSus;

namespace FazTudo.Tests;

public class ModuleCatalogTests
{
    private static ModuleCatalog CatalogWithSia()
    {
        var catalog = new ModuleCatalog();
        catalog.RegisterModule(new SiaSusModule());
        return catalog;
    }

    [Fact]
    public void Registra_o_modulo_uma_vez_so()
    {
        var catalog = CatalogWithSia();
        Assert.False(catalog.RegisterModule(new SiaSusModule()));
        Assert.Single(catalog.Modules);
    }

    [Fact]
    public void Busca_vazia_retorna_toda_a_base()
    {
        var catalog = CatalogWithSia();
        Assert.Equal(catalog.Modules[0].GetKnowledgeBase().Count, catalog.SearchGlobalKnowledgeBase("").Count);
    }

    [Fact]
    public void Busca_ignora_acentos_e_exige_todos_os_termos()
    {
        var catalog = CatalogWithSia();
        Assert.Contains(catalog.SearchGlobalKnowledgeBase("habilitacao portaria"), e => e.Id == "sia-cnes-hab");
        Assert.Contains(catalog.SearchGlobalKnowledgeBase("firebird 3050"), e => e.Id == "sia-ti-firebird");
        Assert.Empty(catalog.SearchGlobalKnowledgeBase("firebird mamografia"));
    }

    [Fact]
    public void Plugin_invalido_nao_derruba_o_catalogo()
    {
        var dir = Directory.CreateTempSubdirectory("plugins-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "quebrado.dll"), "isto não é uma dll");
            var catalog = CatalogWithSia();
            Assert.Equal(0, catalog.DiscoverPluginsFromFolder(dir));
            Assert.Single(catalog.Modules);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Ids_usados_pelo_triador_e_doutor_existem_na_base()
    {
        var module = new SiaSusModule();
        module.Initialize();
        var ids = module.GetKnowledgeBase().Select(e => e.Id).ToHashSet();

        foreach (var id in new[] { "sia-bpa-001", "sia-bpa-002", "sia-bpa-004", "sia-bpa-005", "sia-bpa-006", "sia-bpa-cns",
                     "sia-bpa-raca", "sia-bpa-cid", "sia-ti-virtualstore", "sia-ti-firebird", "sia-ti-bde" })
        {
            Assert.Contains(id, ids);
        }
    }
}
