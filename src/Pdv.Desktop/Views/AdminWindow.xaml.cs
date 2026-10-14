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
        // Abre na primeira aba que o usuário pode ver.
        Loaded += (_, _) => Tabs.SelectedItem = Tabs.Items.OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(t => t.Visibility == Visibility.Visible);
    }

    private void OnTaxProfileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanManageCompany) _viewModel.EditSelectedTaxProfileCommand.Execute(null);
    }

    private void OnProductDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanManageProducts) _viewModel.EditSelectedProductCommand.Execute(null);
    }
}
