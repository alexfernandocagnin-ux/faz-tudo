using System.Windows;
using System.Windows.Controls;
using FazTudo.Core.Models;

namespace FazTudo.Desktop.Views;

public partial class KnowledgeView : UserControl
{
    private const string AllLayers = "Todas as camadas";
    private readonly AppServices _services;
    private string _layer = AllLayers;

    public KnowledgeView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var all = services.Catalog.SearchGlobalKnowledgeBase(null);
        LeadText.Text = $"{all.Count} erros de faturamento com causa e passo a passo de correção, do SIGTAP ao Transmissor.";

        foreach (var layer in new[] { AllLayers }.Concat(all.Select(e => e.Layer).Distinct()))
        {
            var chip = new RadioButton
            {
                Content = layer,
                Style = (Style)FindResource("Chip"),
                GroupName = "KnowledgeLayer",
                IsChecked = layer == AllLayers
            };
            chip.Checked += (_, _) =>
            {
                _layer = layer;
                Refresh();
            };
            LayerChips.Children.Add(chip);
        }

        Refresh();
    }

    public void Select(string knowledgeId)
    {
        SearchBox.Text = string.Empty;
        foreach (RadioButton chip in LayerChips.Children)
        {
            chip.IsChecked = (string)chip.Content == AllLayers;
        }

        Refresh();
        var entry = EntryList.Items.OfType<DiagnosticErrorEntry>()
            .FirstOrDefault(e => string.Equals(e.Id, knowledgeId, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            EntryList.SelectedItem = entry;
            EntryList.ScrollIntoView(entry);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (EntryList is null)
        {
            return;
        }

        var selected = EntryList.SelectedItem;
        var results = _services.Catalog.SearchGlobalKnowledgeBase(SearchBox.Text)
            .Where(e => _layer == AllLayers || e.Layer == _layer)
            .ToList();

        EntryList.ItemsSource = results;
        EntryList.SelectedItem = selected is DiagnosticErrorEntry s && results.Contains(s) ? s : results.FirstOrDefault();
        NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Detail.Visibility = EntryList.SelectedItem is null ? Visibility.Hidden : Visibility.Visible;
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Detail.Visibility = EntryList.SelectedItem is null ? Visibility.Hidden : Visibility.Visible;
        DetailScroll.ScrollToTop();
    }
}
