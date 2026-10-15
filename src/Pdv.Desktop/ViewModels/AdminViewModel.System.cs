using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Licensing;
using Pdv.Application.Maintenance;
using Pdv.Application.Terminals;
using Pdv.Domain.Identity;

namespace Pdv.Desktop.ViewModels;

/// <summary>Retaguarda — aba Sistema: servidor da loja, caixas conectados e backups.</summary>
public partial class AdminViewModel
{
    public bool CanManageSystem => state.User!.Can(Permissions.ManageSystem);

    public ObservableCollection<TerminalRow> Terminals { get; } = [];
    public ObservableCollection<BackupRow> Backups { get; } = [];

    [ObservableProperty] private TerminalRow? _selectedTerminal;
    [ObservableProperty] private BackupRow? _selectedBackup;
    [ObservableProperty] private string _serverSummary = "";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private bool _backupEnabled = true;
    [ObservableProperty] private string _backupTime = "22:00";
    [ObservableProperty] private string _backupRetentionDays = "30";
    [ObservableProperty] private string _backupCopyFolder = "";
    [ObservableProperty] private string _backupStatusText = "";
    [ObservableProperty] private bool _backupHasError;
    [ObservableProperty] private string _licenseSummary = "";
    [ObservableProperty] private string _licenseDetail = "";
    [ObservableProperty] private string _machineCode = "";
    [ObservableProperty] private bool _licenseHasProblem;

    private async Task LoadSystemAsync()
    {
        if (!CanManageSystem) return;
        var info = await Runner.RunAsync<IServerAdmin, ServerInfo>(s => s.GetInfoAsync(ActorId, default));
        ServerSummary = $"Servidor {info.MachineName} · versão {info.Version} · no ar desde {info.StartedAt.ToLocalTime():dd/MM HH:mm} · dados em {info.DataFolder}";
        PairingCode = info.PairingCode ?? "";
        ApplyLicense(await Runner.RunAsync<LicenseService, LicenseStatus>(s => s.GetStatusAsync(ActorId, default)));
        await LoadTerminalsAsync();
        ApplyBackupStatus(await Runner.RunAsync<IBackupManager, BackupStatus>(b => b.GetStatusAsync(ActorId, default)), loadSettings: true);
    }

    private async Task LoadTerminalsAsync()
    {
        var list = await Runner.RunAsync<TerminalAdminService, IReadOnlyList<TerminalListItem>>(s => s.ListAsync(ActorId, default));
        Terminals.Clear();
        foreach (var t in list) Terminals.Add(new TerminalRow(t, t.Id == state.Terminal.Id));
    }

    private void ApplyBackupStatus(BackupStatus status, bool loadSettings)
    {
        if (loadSettings)
        {
            BackupEnabled = status.Settings.Enabled;
            BackupTime = status.Settings.DailyAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            BackupRetentionDays = status.Settings.RetentionDays.ToString(CultureInfo.InvariantCulture);
            BackupCopyFolder = status.Settings.CopyFolder ?? "";
        }
        Backups.Clear();
        foreach (var b in status.Files) Backups.Add(new BackupRow(b));
        BackupHasError = status.LastError is not null;
        BackupStatusText = status.LastError
            ?? (status.LastBackupAt is { } last
                ? $"Último backup: {last.ToLocalTime():dd/MM/yyyy HH:mm} · {status.Files.Count} arquivo(s) em {status.Folder}"
                : "Nenhum backup feito ainda.");
    }

    private void ApplyLicense(LicenseStatus s)
    {
        LicenseSummary = s.State switch
        {
            LicenseState.Trial => $"Demonstração — {s.DaysLeft} dia(s) restante(s)",
            LicenseState.Active => $"Licença ativa — {s.Customer}",
            LicenseState.Grace => "Licença vencida (em tolerância)",
            _ => "Vendas bloqueadas — licença necessária",
        };
        LicenseDetail = $"{s.Plan} · {s.UsedTerminals} de {s.MaxTerminals} caixa(s) · " +
            (s.ExpiresAt is { } e ? $"validade {e:dd/MM/yyyy}" : "sem vencimento") + $"\n{s.Message}";
        MachineCode = s.MachineCode ?? "";
        LicenseHasProblem = s.State is LicenseState.Grace or LicenseState.Restricted || s.Warning is not null;
    }

    [RelayCommand]
    private async Task InstallLicenseAsync()
    {
        var file = Dialogs.PickFile("Licença do Sistema PDV", "Licença (*.pdvlic)|*.pdvlic");
        if (file is null) return;
        var text = await System.IO.File.ReadAllTextAsync(file);
        var result = await Runner.RunAsync<LicenseService, Result<LicenseStatus>>(s => s.InstallAsync(ActorId, text, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        ApplyLicense(result.Value!);
        Dialogs.ShowInfo($"Licença instalada.\n{result.Value!.Message}");
    }

    [RelayCommand]
    private void CopyMachineCode()
    {
        if (MachineCode.Length == 0) return;
        System.Windows.Clipboard.SetText(MachineCode);
        Dialogs.ShowInfo($"Código da máquina copiado: {MachineCode}\nEnvie ao fornecedor para emitir ou transferir a licença.");
    }

    /// <summary>Logs do servidor e deste caixa num .zip para enviar ao suporte (sem senhas, chaves ou certificados).</summary>
    [RelayCommand]
    private async Task ExportSupportPackageAsync()
    {
        var path = Dialogs.PickSaveFile("Pacote para o suporte", "Arquivo compactado (*.zip)|*.zip",
            $"suporte-pdv-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (path is null) return;
        var server = await Runner.RunAsync<IServerAdmin, byte[]>(s => s.ExportSupportPackageAsync(ActorId, default));
        await System.IO.File.WriteAllBytesAsync(path, server);
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            var summary = zip.CreateEntry("resumo-caixa.txt");
            using (var writer = new System.IO.StreamWriter(summary.Open()))
                writer.Write($"Caixa {state.Terminal.Code} ({state.Terminal.MachineName}) | versão {Application.Remote.RemoteApi.Version} | " +
                    $"{Environment.OSVersion} | operador {state.User!.Username} | {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            var logs = System.IO.Path.Combine(Services.LocalSettings.Folder, "logs");
            if (System.IO.Directory.Exists(logs))
                foreach (var file in new System.IO.DirectoryInfo(logs).GetFiles("*.log").Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7)))
                {
                    using var source = new System.IO.FileStream(file.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                        System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                    using var target = zip.CreateEntry($"logs-caixa-{state.Terminal.Code}/{file.Name}").Open();
                    source.CopyTo(target);
                }
        }
        Dialogs.ShowInfo($"Pacote para o suporte gravado em:\n{path}\n\nContém logs do servidor e deste caixa e um resumo técnico — sem senhas, chaves ou certificados.");
    }

    [RelayCommand]
    private async Task RegeneratePairingCodeAsync()
    {
        if (!Dialogs.Confirm("Gerar um novo código de conexão?\n\nOs caixas já conectados continuam funcionando; só novos caixas precisam do código novo.")) return;
        var result = await Runner.RunAsync<IServerAdmin, Result<string>>(s => s.RegeneratePairingCodeAsync(ActorId, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        PairingCode = result.Value!;
    }

    [RelayCommand]
    private async Task RevokeTerminalAsync()
    {
        if (SelectedTerminal is not { } row) { Dialogs.ShowError("Selecione um caixa na lista."); return; }
        if (!Dialogs.Confirm($"Desconectar o caixa {row.Code} ({row.MachineName})?\n\nEle deixa de acessar o servidor e só volta com o código de conexão.")) return;
        var target = row.Item.Id;
        var current = state.Terminal.Id;
        var result = await Runner.RunAsync<TerminalAdminService, Result<bool>>(s => s.RevokeAsync(ActorId, target, current, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        await LoadTerminalsAsync();
    }

    [RelayCommand]
    private async Task SaveBackupSettingsAsync()
    {
        if (!TimeOnly.TryParseExact(BackupTime.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            Dialogs.ShowError("Horário inválido. Use HH:MM, por exemplo 22:00.");
            return;
        }
        if (!int.TryParse(BackupRetentionDays, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1)
        {
            Dialogs.ShowError("Informe por quantos dias manter os backups (1 ou mais).");
            return;
        }
        var settings = new BackupSettings(BackupEnabled, at, days, string.IsNullOrWhiteSpace(BackupCopyFolder) ? null : BackupCopyFolder.Trim());
        var result = await Runner.RunAsync<IBackupManager, Result<BackupStatus>>(b => b.SaveSettingsAsync(ActorId, settings, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        ApplyBackupStatus(result.Value!, loadSettings: true);
        Dialogs.ShowInfo("Configuração de backup salva no servidor.");
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        var result = await Runner.RunAsync<IBackupManager, Result<BackupFileInfo>>(b => b.BackupNowAsync(ActorId, default));
        ApplyBackupStatus(await Runner.RunAsync<IBackupManager, BackupStatus>(b => b.GetStatusAsync(ActorId, default)), loadSettings: false);
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        Dialogs.ShowInfo($"Backup concluído: {result.Value!.Name} ({BackupRow.Size(result.Value.SizeBytes)})." +
            (BackupHasError ? $"\n\nAtenção: {BackupStatusText}" : result.Value.CopiedToSecondFolder ? "\nCópia gravada na segunda pasta." : ""));
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (SelectedBackup is not { } row) { Dialogs.ShowError("Selecione um backup na lista."); return; }
        var confirmation = Dialogs.Prompt("Restaurar backup",
            $"ATENÇÃO: o banco da loja volta a ser como estava em {row.When}.\n" +
            "Vendas, caixas e cadastros feitos depois disso deixam de existir (o estado atual é guardado num backup de segurança).\n" +
            "Os caixas ficam parados durante a restauração.\n\nPara confirmar, digite RESTAURAR:");
        if (confirmation is null) return;
        var name = row.Item.Name;
        var result = await Runner.RunAsync<IBackupManager, Result<string>>(b => b.RestoreAsync(ActorId, name, confirmation, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        Dialogs.ShowInfo(result.Value! + "\n\nFeche e abra o sistema nos caixas para recarregar os dados.");
        ApplyBackupStatus(await Runner.RunAsync<IBackupManager, BackupStatus>(b => b.GetStatusAsync(ActorId, default)), loadSettings: false);
    }

    public sealed record TerminalRow(TerminalListItem Item, bool IsCurrent)
    {
        public string Code => Item.Code;
        public string MachineName => IsCurrent ? $"{Item.MachineName} (este)" : Item.MachineName;
        public string Status => Item.IsPaired ? "Conectado" : "Desconectado";
        public bool IsPaired => Item.IsPaired;
        public string LastSeen => Item.LastSeenAt?.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture) ?? "—";
        public string Version => Item.AppVersion ?? "—";
    }

    public sealed record BackupRow(BackupFileInfo Item)
    {
        public string Name => Item.Name;
        public string When => Item.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        public string SizeText => Size(Item.SizeBytes);
        public string Copy => Item.CopiedToSecondFolder ? "Sim" : "—";

        public static string Size(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:N1} MB"
            : $"{Math.Max(1, bytes / 1024):N0} KB";
    }
}
