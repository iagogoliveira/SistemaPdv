using System.Windows;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class ReturnDialog : Window
{
    private readonly ReturnForm _form;

    public ReturnDialog(ReturnForm form)
    {
        InitializeComponent();
        DataContext = _form = form;
    }

    public Dictionary<int, decimal> Quantities { get; private set; } = [];

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!_form.TryBuild(out var quantities, out var error))
        {
            new MessageDialog(error ?? "", isError: true) { Owner = this }.ShowDialog();
            return;
        }
        Quantities = quantities;
        DialogResult = true;
    }
}
