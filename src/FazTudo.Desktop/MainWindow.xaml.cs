using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FazTudo.Core.Services;
using FazTudo.Desktop.Views;
using FazTudo.Modules.SiaSus;

namespace FazTudo.Desktop;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly SolverView _solver;
    private readonly KnowledgeView _knowledge;
    private readonly SigtapView _sigtap;
    private readonly TriadorView _triador;
    private readonly DoctorView _doctor;
    private readonly ModulesView _modules;

    public MainWindow()
    {
        InitializeComponent();

        var catalog = new ModuleCatalog();
        var sia = new SiaSusModule();
        catalog.RegisterModule(sia);
        catalog.DiscoverPluginsFromFolder(Path.Combine(AppContext.BaseDirectory, "plugins"));
        _services = new AppServices(catalog, sia);

        _solver = new SolverView(_services);
        _knowledge = new KnowledgeView(_services);
        _sigtap = new SigtapView(_services);
        _triador = new TriadorView(_services);
        _doctor = new DoctorView(_services);
        _modules = new ModulesView(_services);

        _services.KnowledgeRequested += id =>
        {
            NavKnowledge.IsChecked = true;
            _knowledge.Select(id);
        };
        _services.SigtapChanged += UpdateSigtapStatus;

        StateChanged += (_, _) => UpdateMaximizedState();
        UpdateSigtapStatus();
        NavSolver.IsChecked = true;
        Loaded += async (_, _) => await HandleCommandLineAsync();
    }

    /// <summary>
    /// "FazTudo.Desktop.exe R0350.OUT" abre o relatório no Solucionador; outro arquivo abre como remessa no Triador
    /// (também vale arrastar o arquivo sobre o .exe).
    /// "--capture &lt;pasta&gt; [arquivos…]" salva um PNG de cada tela e fecha (usado para revisar o visual).
    /// </summary>
    private async Task HandleCommandLineAsync()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length >= 2 && args[0] == "--capture")
        {
            var files = args.Skip(2).ToList();
            await CaptureAllAsync(args[1],
                files.FirstOrDefault(f => !IsSiaReport(f)),
                files.Where(IsSiaReport).ToList());
            Close();
            return;
        }

        if (args.Length >= 1 && args.All(File.Exists))
        {
            if (args.All(IsSiaReport))
            {
                NavSolver.IsChecked = true;
                await _solver.SolveAsync(args);
            }
            else
            {
                NavTriador.IsChecked = true;
                await _triador.AnalyzeAsync(args[0]);
            }
        }
    }

    private static bool IsSiaReport(string path) => path.EndsWith(".OUT", StringComparison.OrdinalIgnoreCase);

    private async Task CaptureAllAsync(string folder, string? sampleBpa, IReadOnlyList<string> sampleReports)
    {
        Directory.CreateDirectory(folder);
        async Task Snap(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(RootGrid.ActualWidth * dpi.DpiScaleX), (int)(RootGrid.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(RootGrid);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            await using var file = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(file);
        }

        NavSolver.IsChecked = true; await Snap("0-solucionador-vazio");
        if (sampleReports.Count > 0)
        {
            await _solver.SolveAsync(sampleReports);
            await Snap("0-solucionador-resposta");
        }

        NavKnowledge.IsChecked = true; await Snap("1-base-de-erros");
        NavSigtap.IsChecked = true; await Snap("2-sigtap");
        NavTriador.IsChecked = true; await Snap("3-triador-vazio");
        if (sampleBpa is not null && File.Exists(sampleBpa))
        {
            await _triador.AnalyzeAsync(sampleBpa);
            await Snap("4-triador-resultado");
            _triador.ShowLinesTab();
            await Snap("5-triador-linhas");
        }

        NavDoctor.IsChecked = true; await Snap("6-doutor-vazio");
        await _doctor.RunAsync();
        await Snap("7-doutor-resultado");
        NavModules.IsChecked = true; await Snap("8-modulos");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost is null)
        {
            return;
        }

        PageHost.Content = sender switch
        {
            _ when sender == NavSolver => _solver,
            _ when sender == NavKnowledge => _knowledge,
            _ when sender == NavSigtap => _sigtap,
            _ when sender == NavTriador => _triador,
            _ when sender == NavDoctor => _doctor,
            _ => _modules
        };
    }

    private void UpdateSigtapStatus()
    {
        var sigtap = _services.Sia.Sigtap;
        SigtapStatusText.Text = sigtap.IsOfficialTableLoaded ? sigtap.LoadedSourceLabel : "Base de exemplo — importe a tabela oficial";
        SigtapDot.Fill = sigtap.IsOfficialTableLoaded ? Palette.Brush("#22C55E") : Palette.Brush("#F59E0B");
    }

    private void SigtapStatus_Click(object sender, MouseButtonEventArgs e) => NavSigtap.IsChecked = true;

    // ---------- Barra de título própria ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizedState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // Janela sem moldura maximizada "vaza" pelas bordas da tela: compensa com margem.
        RootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }
}
