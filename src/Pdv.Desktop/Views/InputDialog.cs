using System.Windows;
using System.Windows.Controls;

namespace Pdv.Desktop.Views;

/// <summary>Diálogo simples de entrada de texto ou senha (valores de caixa, motivos).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox? _text;
    private readonly PasswordBox? _password;

    public InputDialog(string title, string message, string initialValue, bool isPassword = false)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 14;

        Control input = isPassword
            ? _password = new PasswordBox { FontSize = 18, Margin = new Thickness(0, 6, 0, 12) }
            : _text = new TextBox { Text = initialValue, FontSize = 18, Margin = new Thickness(0, 6, 0, 12) };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(DialogButtons.Create(this));
        Content = panel;

        Loaded += (_, _) =>
        {
            input.Focus();
            _text?.SelectAll();
        };
    }

    public string Value => _text?.Text ?? _password!.Password;
}

/// <summary>Usuário e senha de quem libera uma operação restrita.</summary>
public sealed class CredentialsDialog : Window
{
    private readonly TextBox _username = new() { FontSize = 16, Margin = new Thickness(0, 4, 0, 10) };
    private readonly PasswordBox _password = new() { FontSize = 16, Margin = new Thickness(0, 4, 0, 12) };

    public CredentialsDialog(string title, string message)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 14;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Usuário do supervisor" });
        panel.Children.Add(_username);
        panel.Children.Add(new TextBlock { Text = "Senha" });
        panel.Children.Add(_password);
        panel.Children.Add(DialogButtons.Create(this));
        Content = panel;
        Loaded += (_, _) => _username.Focus();
    }

    public string Username => _username.Text;

    public string Password => _password.Password;
}

/// <summary>Pergunta Sim/Não em português, independente do idioma do Windows.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string message)
    {
        Title = "Sistema PDV";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 15;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(DialogButtons.Create(this, "Sim", "Não"));
        Content = panel;
    }
}

internal static class DialogButtons
{
    public static StackPanel Create(Window window, string okText = "OK", string cancelText = "Cancelar")
    {
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, Style = (Style)window.FindResource("PrimaryButton") };
        var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => window.DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        return buttons;
    }
}
