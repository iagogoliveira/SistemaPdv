using System.Windows;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.LoggedIn += (_, _) => DialogResult = true;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    // PasswordBox.Password não é bindable (por segurança); passamos o valor direto ao comando.
    private void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SubmitCommand.CanExecute(PasswordBox.Password))
            _viewModel.SubmitCommand.Execute(PasswordBox.Password);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
