using System.Windows;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class TaxProfileEditDialog : Window
{
    public TaxProfileEditDialog(TaxProfileForm form)
    {
        InitializeComponent();
        DataContext = form;
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
