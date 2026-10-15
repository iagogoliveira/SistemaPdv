using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pdv.Desktop.Controls;

namespace Pdv.Desktop.Views;

/// <summary>Diálogo simples de entrada de texto ou senha (valores de caixa, motivos).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox? _text;
    private readonly PasswordBox? _password;

    public InputDialog(string title, string message, string initialValue, bool isPassword = false)
    {
        DialogChrome.Apply(this, title, 420);

        Control input = isPassword
            ? _password = new PasswordBox { FontSize = 18, Margin = new Thickness(0, 10, 0, 16) }
            : _text = new TextBox { Text = initialValue, FontSize = 18, Margin = new Thickness(0, 10, 0, 16) };

        var panel = DialogChrome.Panel(this, title, isPassword ? "Icon.Lock" : "Icon.Edit");
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = DialogChrome.Brush(this, "TextSecondaryBrush") });
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
        DialogChrome.Apply(this, title, 420);

        var panel = DialogChrome.Panel(this, title, "Icon.Shield");
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            Foreground = DialogChrome.Brush(this, "TextSecondaryBrush") });
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
        DialogChrome.Apply(this, "Sistema PDV", 420);
        FontSize = 15;

        var panel = DialogChrome.Panel(this, "Confirmação", "Icon.Help");
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(DialogButtons.Create(this, "Sim", "Não"));
        Content = panel;
    }
}

/// <summary>Mensagem de informação ou de alerta no visual do sistema (substitui a MessageBox do Windows).</summary>
public sealed class MessageDialog : Window
{
    public MessageDialog(string message, bool isError)
    {
        DialogChrome.Apply(this, "Sistema PDV", 440);
        FontSize = 15;
        var panel = DialogChrome.Panel(this, isError ? "Atenção" : "Informação", isError ? "Icon.Alert" : "Icon.Info");
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90, Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } });
        Content = panel;
        Loaded += (_, _) => ok.Focus();
    }
}

/// <summary>Aparência comum dos diálogos: fundo, fonte e cabeçalho com ícone.</summary>
internal static class DialogChrome
{
    public static void Apply(Window window, string title, double width)
    {
        window.Title = title;
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;
        window.FontSize = 14;
        window.UseLayoutRounding = true;
        window.FontFamily = (FontFamily)window.FindResource("UiFont");
        window.Background = Brush(window, "SurfaceBrush");
        window.Foreground = Brush(window, "TextBrush");
    }

    public static StackPanel Panel(Window window, string heading, string iconKey)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = Brush(window, "BrandSoftBrush"),
            Child = new IconView { Data = (Geometry)window.FindResource(iconKey), Foreground = Brush(window, "BrandBrush"), Width = 22, Height = 22 },
        });
        header.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(header);
        return panel;
    }

    public static Brush Brush(Window window, string key) => (Brush)window.FindResource(key);
}

internal static class DialogButtons
{
    public static StackPanel Create(Window window, string okText = "OK", string cancelText = "Cancelar")
    {
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, Style = (Style)window.FindResource("PrimaryButton") };
        var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => window.DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, -4, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        return buttons;
    }
}
