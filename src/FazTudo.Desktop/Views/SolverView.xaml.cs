using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FazTudo.Modules.SiaSus.Reports;
using Microsoft.Win32;

namespace FazTudo.Desktop.Views;

public partial class SolverView : UserControl
{
    private const string DefaultSiaFolder = @"C:\Datasus\SIA";
    private readonly AppServices _services;
    private SolverResult? _result;

    public SolverView(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir relatório de ocorrências da consistência (R0350.OUT)",
            Filter = "Relatórios do SIA (*.OUT;*.TXT)|*.OUT;*.TXT|Todos os arquivos (*.*)|*.*",
            InitialDirectory = Directory.Exists(DefaultSiaFolder) ? DefaultSiaFolder : string.Empty
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = SolveAsync(dialog.FileName);
        }
    }

    public async Task SolveAsync(string path)
    {
        ChooseButton.IsEnabled = false;
        ChooseButtonText.Text = "Lendo…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = await Task.Run(() => _services.Sia.SolveConsistencyReport(path));
            Show(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Não consegui ler esse arquivo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ChooseButton.IsEnabled = true;
            ChooseButtonText.Text = "Escolher relatório";
        }
    }

    private void Show(SolverResult result)
    {
        _result = result;
        var report = result.Report;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultState.Visibility = Visibility.Visible;
        HeaderActions.Visibility = Visibility.Visible;
        ResultState.ScrollToTop();

        FileMeta.Text = string.Join(" · ", new[]
        {
            report.SourceName, report.Orgao, $"competência {report.Competencia}", report.BdsiaVersion,
            report.GeneratedAt.Length > 0 ? $"gerado em {report.GeneratedAt}" : string.Empty
        }.Where(s => s.Length > 0));
        Headline.Text = result.Headline;

        GroupPills.Children.Clear();
        foreach (var group in result.Problems.GroupBy(p => p.Group))
        {
            var converter = (IValueConverter)FindResource("SiaGroupBrush");
            var pill = new Border
            {
                Style = (Style)FindResource("Pill"),
                Background = (Brush)converter.Convert(group.Key, typeof(Brush), "soft", null),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = $"{group.Key}: {group.Sum(p => p.Quantity)} ocorrência(s)",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)converter.Convert(group.Key, typeof(Brush), null, null)
                },
                ToolTip = ConsistencyReport.GroupMeanings.GetValueOrDefault(group.Key, string.Empty)
            };
            GroupPills.Children.Add(pill);
        }

        NotesList.ItemsSource = result.Notes;
        ProblemsList.ItemsSource = result.Problems;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        Clipboard.SetText(_result.ToPlainText());
        CopyButtonText.Text = "Copiado!";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            CopyButtonText.Text = "Copiar resposta";
            timer.Stop();
        };
        timer.Start();
    }

    private void KnowledgeLink_Click(object sender, RoutedEventArgs e) =>
        _services.ShowKnowledge((sender as FrameworkElement)?.Tag as string);

    // ---------- Arrastar e soltar ----------

    private void View_DragEnter(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (ok && EmptyState.Visibility == Visibility.Visible)
        {
            DropOutline.Stroke = (Brush)FindResource("AccentBrush");
            DropOutline.Fill = (Brush)FindResource("AccentSoftBrush");
            DropTitle.Text = "Solte para analisar";
        }
    }

    private void View_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void View_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
        {
            _ = SolveAsync(files[0]);
        }
    }

    private void ResetDropZone()
    {
        DropOutline.Stroke = (Brush)FindResource("LineStrongBrush");
        DropOutline.Fill = Brushes.Transparent;
        DropTitle.Text = "Arraste o relatório do SIA para cá";
    }
}
