using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FazTudo.Core.Models;
using Microsoft.Win32;

namespace FazTudo.Desktop.Views;

public partial class TriadorView : UserControl
{
    private const double BarMaxWidth = 180;
    private readonly AppServices _services;
    private BatchAnalysisSummary? _summary;

    public sealed record GroupItem(RootCauseGroup Group, string Detail, string Procedure, double BarWidth, string ShareText);

    public TriadorView(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir remessa do BPA-Mag",
            Filter = "Remessa BPA (PA*.*;*.txt)|PA*.*;*.txt|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = AnalyzeAsync(dialog.FileName);
        }
    }

    // ---------- Arrastar e soltar ----------

    private void View_DragEnter(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (ok)
        {
            DropOutline.Stroke = (System.Windows.Media.Brush)FindResource("AccentBrush");
            DropOutline.Fill = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
            DropTitle.Text = "Solte para analisar";
        }
    }

    private void View_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void View_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
        {
            _ = AnalyzeAsync(files[0]);
        }
    }

    private void ResetDropZone()
    {
        DropOutline.Stroke = (System.Windows.Media.Brush)FindResource("LineStrongBrush");
        DropOutline.Fill = System.Windows.Media.Brushes.Transparent;
        DropTitle.Text = "Arraste a remessa do BPA-Mag para cá";
    }

    // ---------- Análise ----------

    public async Task AnalyzeAsync(string path)
    {
        ChooseButton.IsEnabled = AnotherButton.IsEnabled = false;
        ChooseButtonText.Text = "Analisando…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var summary = await Task.Run(() => _services.Sia.CreateBpaAnalyzer().Analyze(path));
            Show(summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Não foi possível ler o arquivo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ChooseButton.IsEnabled = AnotherButton.IsEnabled = true;
            ChooseButtonText.Text = "Escolher arquivo";
        }
    }

    private void Show(BatchAnalysisSummary s)
    {
        _summary = s;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultState.Visibility = Visibility.Visible;
        HeaderActions.Visibility = Visibility.Visible;
        ExportButton.IsEnabled = s.DetailedFindings.Count > 0;

        FileName.Text = s.SourceName;
        FileMeta.Text = $"{s.DetectedFormat} · competência {s.ReferenceCompetencia} · valores pela {_services.Sia.Sigtap.LoadedSourceLabel}";

        var totalProcedures = s.TotalRecordsEvaluated;
        var validPct = totalProcedures == 0 ? 0 : (double)s.TotalValidRecords / totalProcedures;
        var totalValue = s.EstimatedGlosaValue + s.ApprovedEstimatedValue;

        KpiTotal.Text = s.TotalRecordsEvaluated.ToString("N0");
        KpiTotalSub.Text = "registros BPA-C e BPA-I";
        KpiValid.Text = s.TotalValidRecords.ToString("N0");
        KpiValidSub.Text = $"{validPct:P0} da remessa · {s.ApprovedEstimatedValue:C}";
        KpiFindings.Text = s.TotalRejectedOccurrences.ToString("N0");
        KpiFindingsSub.Text = $"em {s.TotalRecordsEvaluated - s.TotalValidRecords:N0} linha(s) · {s.RootCauseGroups.Count} causa(s)";
        KpiGlosa.Text = s.EstimatedGlosaValue.ToString("C");
        KpiGlosaSub.Text = totalValue > 0 ? $"{s.EstimatedGlosaValue / totalValue:P0} do valor da remessa" : "sem valor calculado";

        WarningsList.ItemsSource = s.Warnings;

        var maxImpact = s.RootCauseGroups.Count == 0 ? 0 : s.RootCauseGroups.Max(g => g.TotalFinancialImpact);
        GroupsList.ItemsSource = s.RootCauseGroups.Select(g =>
        {
            var parts = g.GroupKey.Split(" · ");
            var detail = parts.Length > 1 ? Capitalize(parts[1]) : g.GroupKey;
            var procedure = parts.Length > 2 ? parts[2] : string.Empty;
            var ratio = maxImpact > 0 ? (double)(g.TotalFinancialImpact / maxImpact) : 0;
            var share = s.EstimatedGlosaValue > 0 ? $"{g.TotalFinancialImpact / s.EstimatedGlosaValue:P0} da glosa" : string.Empty;
            return new GroupItem(g, detail, procedure, Math.Max(4, ratio * BarMaxWidth), share);
        }).ToList();
        LinesGrid.ItemsSource = s.DetailedFindings;

        ShowGroupsText.Text = $"Causas raiz ({s.RootCauseGroups.Count})";
        ShowLinesText.Text = $"Linhas com crítica ({s.DetailedFindings.Count})";
        ShowGroups.IsChecked = true;
        UpdatePanels();
    }

    public void ShowLinesTab() => ShowLines.IsChecked = true;

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_summary is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Salvar linhas com crítica",
            Filter = "Planilha CSV para Excel (*.csv)|*.csv",
            FileName = $"criticas-{Path.GetFileNameWithoutExtension(_summary.SourceName)}.csv"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            FazTudo.Modules.SiaSus.Services.TriagemCsvExporter.Write(_summary, dialog.FileName);
            if (MessageBox.Show(Window.GetWindow(this), $"{_summary.DetailedFindings.Count:N0} linha(s) exportada(s). Abrir agora?",
                    "Exportado", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message + "\n\nSe a planilha estiver aberta no Excel, feche-a e tente de novo.",
                "Não foi possível salvar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    private void ViewToggle_Checked(object sender, RoutedEventArgs e) => UpdatePanels();

    private void UpdatePanels()
    {
        if (GroupsPanel is null)
        {
            return;
        }

        var hasFindings = LinesGrid.Items.Count > 0;
        AllClearPanel.Visibility = hasFindings ? Visibility.Collapsed : Visibility.Visible;
        GroupsPanel.Visibility = hasFindings && ShowGroups.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LinesPanel.Visibility = hasFindings && ShowLines.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KnowledgeLink_Click(object sender, RoutedEventArgs e) =>
        _services.ShowKnowledge((sender as FrameworkElement)?.Tag as string);

    private void LinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        _services.ShowKnowledge((LinesGrid.SelectedItem as RejectedRecordDetail)?.LinkedKnowledgeErrorId);
}
