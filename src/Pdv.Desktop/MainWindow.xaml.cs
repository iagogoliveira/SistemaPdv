using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(CheckoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => UpdateClock();
        clock.Start();
        UpdateClock();
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync();
            CodeBox.Focus();
        };
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);

    // O leitor de código de barras "digita" no campo: devolvemos o foco a ele após qualquer ação.
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key is >= Key.F1 and <= Key.F12 or Key.Escape && e.Key != Key.F9)
            CodeBox.Focus();
        else if (e.Key == Key.F9)
            AmountBox.Focus();
    }
}
