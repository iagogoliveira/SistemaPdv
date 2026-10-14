using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Identity;
using Pdv.Desktop.Services;

namespace Pdv.Desktop.ViewModels;

public partial class LoginViewModel(ScopedRunner runner, AppState state) : ObservableObject
{
    [ObservableProperty]
    private bool _isSetupMode;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;

    public string TerminalLabel => $"Terminal {state.Terminal.Code} · {state.Terminal.MachineName}";

    public event EventHandler? LoggedIn;

    public async Task LoadAsync() =>
        IsSetupMode = !await runner.RunAsync<AuthenticationService, bool>(auth => auth.HasAnyUserAsync());

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync(string password)
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(password))
        {
            Error = "Informe usuário e senha.";
            return;
        }

        IsBusy = true;
        try
        {
            Result<UserSession> result = await runner.RunAsync<AuthenticationService, Result<UserSession>>(auth => IsSetupMode
                ? auth.CreateInitialAdminAsync(Username, DisplayName, password)
                : auth.LoginAsync(Username, password));

            if (!result.Succeeded)
            {
                Error = result.Error;
                return;
            }

            state.User = result.Value;
            LoggedIn?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSubmit() => !IsBusy;
}
