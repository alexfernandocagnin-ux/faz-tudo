using System.Windows;
using System.Windows.Controls;
using FazTudo.Core.Models;

namespace FazTudo.Desktop.Views;

public partial class DoctorView : UserControl
{
    private readonly AppServices _services;

    public DoctorView(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e) => await RunAsync();

    public async Task RunAsync()
    {
        RunButton.IsEnabled = false;
        RunButtonText.Text = "Examinando…";
        try
        {
            var items = await _services.Sia.Doctor.RunAllAsync();

            // Problemas primeiro.
            ResultsList.ItemsSource = items
                .OrderBy(i => i.Status switch
                {
                    CheckHealthStatus.Critical => 0,
                    CheckHealthStatus.Attention => 1,
                    CheckHealthStatus.Info => 2,
                    _ => 3
                })
                .ToList();

            CountOk.Text = items.Count(i => i.Status == CheckHealthStatus.Healthy).ToString();
            CountAttention.Text = items.Count(i => i.Status == CheckHealthStatus.Attention).ToString();
            CountCritical.Text = items.Count(i => i.Status == CheckHealthStatus.Critical).ToString();
            CountInfo.Text = items.Count(i => i.Status == CheckHealthStatus.Info).ToString();

            EmptyState.Visibility = Visibility.Collapsed;
            Summary.Visibility = Visibility.Visible;
            ResultsScroll.Visibility = Visibility.Visible;
        }
        finally
        {
            RunButton.IsEnabled = true;
            RunButtonText.Text = "Examinar novamente";
        }
    }

    private void KnowledgeLink_Click(object sender, RoutedEventArgs e) =>
        _services.ShowKnowledge((sender as FrameworkElement)?.Tag as string);
}
