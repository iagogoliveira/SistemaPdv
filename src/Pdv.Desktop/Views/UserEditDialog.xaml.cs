using System.Windows;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class UserEditDialog : Window
{
    public UserEditDialog(UserForm form)
    {
        InitializeComponent();
        DataContext = form;
    }

    public string Password => PasswordBox.Password;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password != ConfirmBox.Password)
        {
            ErrorText.Text = "As senhas não conferem.";
            return;
        }
        DialogResult = true;
    }
}
