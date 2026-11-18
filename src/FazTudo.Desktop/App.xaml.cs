using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace FazTudo.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Datas e valores (R$) no padrão brasileiro em todos os bindings.
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = ptBr;
        CultureInfo.DefaultThreadCurrentUICulture = ptBr;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(ptBr.IetfLanguageTag)));

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
