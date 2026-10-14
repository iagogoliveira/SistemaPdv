using System.Windows;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class ProductEditDialog : Window
{
    public ProductEditDialog(ProductForm form)
    {
        InitializeComponent();
        DataContext = form;
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
