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
        SizeChanged += (_, _) => AdaptLayout();
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync();
            CodeBox.Focus();
        };
    }

    /// <summary>
    /// Telas pequenas (1366×768, 1024×768): modo compacto em telas baixas e colunas mais estreitas em telas estreitas,
    /// para a forma de pagamento e os itens continuarem visíveis.
    /// </summary>
    private void AdaptLayout()
    {
        var compact = ActualHeight < 900;
        Controls.Layout.SetCompact(this, compact);
        Body.Margin = compact ? new Thickness(16, 10, 16, 10) : new Thickness(20, 16, 20, 16);

        var narrow = ActualWidth < 1250;
        PaymentColumn.Width = new GridLength(narrow ? 330 : 400);
        Controls.Layout.SetTileColumns(this, compact && !narrow ? 3 : 2);
        ColNumber.Width = narrow ? 38 : 48;
        ColQuantity.Width = narrow ? 58 : 80;
        ColUnit.Width = narrow ? 48 : 50;
        ColPrice.Width = narrow ? 86 : 100;
        ColDiscount.Width = narrow ? 86 : 100;
        ColTotal.Width = narrow ? 98 : 120;
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
