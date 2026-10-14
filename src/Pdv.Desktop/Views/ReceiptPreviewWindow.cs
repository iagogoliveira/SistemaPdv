using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pdv.Application.Fiscal;
using Pdv.Application.Sales;
using Pdv.Desktop.Services;

namespace Pdv.Desktop.Views;

/// <summary>Visualização do comprovante na tela, com opção de imprimir (reimpressão ou sem impressora).</summary>
public sealed class ReceiptPreviewWindow : Window
{
    public ReceiptPreviewWindow(ReceiptData receipt, ReceiptPrinter printer, IDialogService dialogs, NfceInfo? fiscal = null)
    {
        Title = fiscal is null ? $"Comprovante — venda nº {receipt.Number}" : $"DANFE NFC-e nº {fiscal.Number} — venda nº {receipt.Number}";
        Height = fiscal is null ? 640 : 760;
        Width = 420;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 14;

        var document = ReceiptPrinter.Build(receipt, printer.Options.PaperWidthMm, fiscal);
        var viewer = new FlowDocumentScrollViewer
        {
            Document = document,
            Background = Brushes.White,
            Margin = new Thickness(16, 16, 16, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var print = new Button { Content = "Imprimir", IsDefault = true, MinWidth = 90, Style = (Style)FindResource("PrimaryButton") };
        print.Click += (_, _) =>
        {
            try
            {
                printer.Print(receipt, fiscal);
                Close();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Falha ao imprimir comprovante {Number}", receipt.Number);
                dialogs.ShowError($"Não foi possível imprimir: {ex.Message}");
            }
        };
        var close = new Button { Content = "Fechar", IsCancel = true, MinWidth = 90 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(close);
        buttons.Children.Add(print);

        var panel = new DockPanel { Background = (Brush)FindResource("PanelBrush") };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(viewer);
        Content = panel;
    }
}
