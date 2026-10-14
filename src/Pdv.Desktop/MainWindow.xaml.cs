using System.Windows;
using System.Windows.Input;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(CheckoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync();
            CodeBox.Focus();
        };
    }

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
