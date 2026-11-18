using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FazTudo.Desktop.Views;

public partial class ModulesView : UserControl
{
    private readonly string _pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");

    public ModulesView(AppServices services)
    {
        InitializeComponent();
        var catalog = services.Catalog;
        LeadText.Text = $"{catalog.Modules.Count} módulo(s) instalado(s). Cada área (SIA, SIHD, SCNES, TISS…) entra como um módulo independente.";
        ModulesList.ItemsSource = catalog.Modules;
        ErrorsList.ItemsSource = catalog.LoadErrors;
        PluginPath.Text = _pluginsDir;
    }

    private void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_pluginsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_pluginsDir}\"") { UseShellExecute = true });
    }
}
