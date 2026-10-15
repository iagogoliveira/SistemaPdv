using System.Windows;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class PurchaseDialog : Window
{
    private readonly PurchaseForm _form;

    public PurchaseDialog(PurchaseForm form)
    {
        InitializeComponent();
        DataContext = _form = form;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_form.IsManual && _form.Supplier is null)
        {
            new MessageDialog("Escolha o fornecedor.", isError: true) { Owner = this }.ShowDialog();
            return;
        }
        if (_form.Lines.Count == 0 || _form.Unlinked > 0)
        {
            new MessageDialog(_form.Lines.Count == 0 ? "Adicione ao menos um item." : "Vincule todos os itens a produtos da loja.", isError: true) { Owner = this }.ShowDialog();
            return;
        }
        if (_form.Lines.Any(l => l.StockQuantity <= 0))
        {
            new MessageDialog("Há itens com quantidade zerada.", isError: true) { Owner = this }.ShowDialog();
            return;
        }
        DialogResult = true;
    }
}
