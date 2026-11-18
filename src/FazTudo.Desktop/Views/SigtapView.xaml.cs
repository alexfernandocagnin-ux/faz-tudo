using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FazTudo.Desktop.Views;

public partial class SigtapView : UserControl
{
    private readonly AppServices _services;
    private string _instrument = "Todos";

    public SigtapView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        foreach (var instrument in new[] { "Todos", "BPA-C", "BPA-I", "APAC", "RAAS", "AIH" })
        {
            var chip = new RadioButton
            {
                Content = instrument == "Todos" ? "Todos os instrumentos" : instrument,
                Style = (Style)FindResource("Chip"),
                GroupName = "SigtapInstrument",
                IsChecked = instrument == "Todos"
            };
            chip.Checked += (_, _) =>
            {
                _instrument = instrument;
                Refresh();
            };
            InstrumentChips.Children.Add(chip);
        }

        Refresh();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (ProcGrid is null)
        {
            return;
        }

        var sigtap = _services.Sia.Sigtap;
        var results = sigtap.Search(SearchBox.Text, _instrument);
        ProcGrid.ItemsSource = results;

        var total = sigtap.AllProcedures.Count;
        CountText.Text = results.Count >= 250
            ? $"Mostrando os primeiros 250 de {total:N0} — refine a busca"
            : $"{results.Count:N0} de {total:N0} procedimentos";
        NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsHint.Text = sigtap.IsOfficialTableLoaded
            ? "Confira o código ou tente parte do nome."
            : "A base de exemplo tem poucos procedimentos. Importe a tabela oficial para consultar todos.";

        if (sigtap.IsOfficialTableLoaded)
        {
            SourceBanner.Background = Palette.SuccessSoft;
            SourceBanner.BorderBrush = Palette.Brush("#BBF7D0");
            SourceIcon.Text = "";
            SourceIcon.Foreground = Palette.Success;
            SourceTitle.Text = sigtap.LoadedSourceLabel;
            SourceTitle.Foreground = Palette.Brush("#14532D");
            SourceText.Text = "Tabela oficial carregada e salva neste computador — continua aqui quando você reabrir o programa. " +
                              "Para trocar de competência, importe o zip do mês desejado.";
            ImportButtonText.Text = "Importar outra competência";
        }
        else
        {
            SourceBanner.Background = Palette.WarningSoft;
            SourceBanner.BorderBrush = Palette.Brush("#FDE68A");
            SourceIcon.Text = "";
            SourceIcon.Foreground = Palette.Warning;
            SourceTitle.Text = "Você está vendo a base de exemplo";
            SourceTitle.Foreground = Palette.Brush("#78350F");
            SourceText.Text = "São só alguns procedimentos, com valores ilustrativos. Baixe o zip do mês em sigtap.datasus.gov.br " +
                              "(Tabela Unificada) e clique em \"Importar zip do SIGTAP\" — ou arraste o zip para esta tela.";
            ImportButtonText.Text = "Importar zip do SIGTAP";
        }
    }

    private void ImportZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha o zip da Tabela Unificada do SIGTAP",
            Filter = "Tabela Unificada SIGTAP (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = ImportAsync(dialog.FileName);
        }
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta descompactada do SIGTAP (contém tb_procedimento.txt)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = ImportAsync(dialog.FolderName);
        }
    }

    private void View_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void View_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            _ = ImportAsync(paths[0]);
        }
    }

    public async Task ImportAsync(string zipOrFolder)
    {
        ImportButton.IsEnabled = ImportFolderButton.IsEnabled = false;
        ImportButtonText.Text = "Importando…";
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var count = await Task.Run(() => _services.Sia.ImportSigtap(zipOrFolder));
            _services.NotifySigtapChanged();
            MessageBox.Show(Window.GetWindow(this), $"{count:N0} procedimentos importados.", "SIGTAP",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Não foi possível importar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            ImportButton.IsEnabled = ImportFolderButton.IsEnabled = true;
            Refresh();
        }
    }
}
