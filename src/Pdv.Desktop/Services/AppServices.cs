using Pdv.Client;
using System.Collections;
using System.Windows;
using Pdv.Application;
using Pdv.Application.Fiscal;
using Pdv.Application.Identity;
using Pdv.Application.Sales;
using Pdv.Desktop.Views;
using Pdv.Application.Terminals;

namespace Pdv.Desktop.Services;

/// <summary>Estado da sessão do caixa: terminal identificado pelo servidor e usuário logado.</summary>
public sealed class AppState
{
    public TerminalInfo Terminal { get; set; } = null!;
    public UserSession? User { get; set; }
}

/// <summary>
/// Libera operações restritas: usa o próprio operador se ele tiver a permissão; caso contrário,
/// pede usuário e senha de um supervisor. A permissão é validada de novo no backend.
/// </summary>
public sealed class SupervisorAuthorizer(ServerRunner runner, AppState state, IDialogService dialogs)
{
    public async Task<Guid?> AuthorizeAsync(string permission, string action)
    {
        if (state.User!.Can(permission)) return state.User.UserId;

        while (true)
        {
            var credentials = dialogs.PromptCredentials("Liberação do supervisor", $"{action} exige liberação de um supervisor.");
            if (credentials is null) return null;

            var result = await runner.RunAsync<AuthenticationService, Result<Guid>>(a =>
                a.AuthorizeAsync(credentials.Value.Username, credentials.Value.Password, permission));
            if (result.Succeeded) return result.Value;
            dialogs.ShowError(result.Error!);
        }
    }
}

public interface IDialogService
{
    void ShowInfo(string message);
    void ShowError(string message);
    string? Prompt(string title, string message, string initialValue = "");
    string? PromptPassword(string title, string message);
    (string Username, string Password)? PromptCredentials(string title, string message);
    T? Pick<T>(string title, IEnumerable<T> items, IReadOnlyList<GridColumn> columns, Func<T, bool>? canSelect = null) where T : class;
    void ShowGrid(string title, IEnumerable items, IReadOnlyList<GridColumn> columns);
    void ShowReceipt(ReceiptData receipt, NfceInfo? fiscal = null);
    bool Confirm(string message);
    string? PickFile(string title, string filter);
    string? PickSaveFile(string title, string filter, string fileName);
}

public sealed class DialogService(ReceiptPrinter printer) : IDialogService
{
    private static Window? Owner => System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current.MainWindow;

    public void ShowInfo(string message) => Show(message, MessageBoxImage.Information);

    public void ShowError(string message) => Show(message, MessageBoxImage.Warning);

    public string? Prompt(string title, string message, string initialValue = "")
    {
        var dialog = new InputDialog(title, message, initialValue) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public string? PromptPassword(string title, string message)
    {
        var dialog = new InputDialog(title, message, "", isPassword: true) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public (string Username, string Password)? PromptCredentials(string title, string message)
    {
        var dialog = new CredentialsDialog(title, message) { Owner = Owner };
        return dialog.ShowDialog() == true ? (dialog.Username, dialog.Password) : null;
    }

    public T? Pick<T>(string title, IEnumerable<T> items, IReadOnlyList<GridColumn> columns, Func<T, bool>? canSelect = null) where T : class
    {
        var dialog = new GridDialog(title, items, columns, selectable: true, canSelect is null ? null : o => canSelect((T)o)) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.SelectedItem as T : null;
    }

    public void ShowGrid(string title, IEnumerable items, IReadOnlyList<GridColumn> columns) =>
        new GridDialog(title, items, columns, selectable: false) { Owner = Owner }.ShowDialog();

    public void ShowReceipt(ReceiptData receipt, NfceInfo? fiscal = null) =>
        new ReceiptPreviewWindow(receipt, printer, this, fiscal) { Owner = Owner }.ShowDialog();

    public bool Confirm(string message) => new ConfirmDialog(message) { Owner = Owner }.ShowDialog() == true;

    public string? PickFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string fileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private static void Show(string message, MessageBoxImage image) =>
        new MessageDialog(message, image == MessageBoxImage.Warning) { Owner = Owner }.ShowDialog();
}
