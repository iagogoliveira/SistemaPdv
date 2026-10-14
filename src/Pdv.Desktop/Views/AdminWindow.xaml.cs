using System.Windows;
using System.Windows.Input;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class AdminWindow : Window
{
    private readonly AdminViewModel _viewModel;

    public AdminWindow(AdminViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        if (!viewModel.ShowProductsTab) Tabs.SelectedIndex = 1;
    }

    private void OnProductDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanManageProducts) _viewModel.EditSelectedProductCommand.Execute(null);
    }
}
