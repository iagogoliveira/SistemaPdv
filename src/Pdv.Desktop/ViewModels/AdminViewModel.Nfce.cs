using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Fiscal;
using Pdv.Application.Sales;
using Pdv.Desktop.Services;
using Pdv.Domain.Fiscal;

namespace Pdv.Desktop.ViewModels;

/// <summary>Retaguarda — aba NFC-e: configuração da máquina, teste da SEFAZ e acompanhamento das notas.</summary>
public partial class AdminViewModel
{
    public IReadOnlyList<Choice<FiscalEnvironment>> Environments { get; } =
    [
        new(FiscalEnvironment.Homologation, "Homologação (testes, sem valor fiscal)"),
        new(FiscalEnvironment.Production, "Produção (notas com valor fiscal)"),
    ];

    public ObservableCollection<NfceRow> NfceDocuments { get; } = [];

    [ObservableProperty] private NfceRow? _selectedNfce;
    [ObservableProperty] private bool _nfceEnabled;
    [ObservableProperty] private Choice<FiscalEnvironment>? _nfceEnvironment;
    [ObservableProperty] private string _nfceSeries = "1";
    [ObservableProperty] private string? _certificatePath;
    [ObservableProperty] private string _cscId = "";
    [ObservableProperty] private string _nfceStatus = "";
    [ObservableProperty] private bool _hasCertificatePassword;
    [ObservableProperty] private bool _hasCscToken;

    /// <summary>Preenchidos pela janela (PasswordBox). Vazio = manter o valor já salvo.</summary>
    public string CertificatePasswordInput { get; set; } = "";
    public string CscTokenInput { get; set; } = "";

    /// <summary>Certificado escolhido neste computador, enviado ao servidor quando a configuração é salva.</summary>
    private byte[]? _pendingCertificate;
    private NfceConfigView? _nfceConfig;

    private async Task LoadNfceAsync()
    {
        if (!CanManageCompany) return;
        var s = _nfceConfig = await Runner.RunAsync<NfceConfigurationService, NfceConfigView>(c => c.GetAsync(ActorId, default));
        NfceEnabled = s.Enabled;
        NfceEnvironment = Environments.First(e => e.Value == s.Environment);
        NfceSeries = s.Series.ToString(CultureInfo.InvariantCulture);
        _pendingCertificate = null;
        CertificatePath = s.HasCertificate ? "Certificado instalado no servidor" : null;
        CscId = s.CscId ?? "";
        HasCertificatePassword = s.HasCertificatePassword;
        HasCscToken = s.HasCscToken;
        await LoadNfceDocumentsAsync();
    }

    private async Task LoadNfceDocumentsAsync()
    {
        var list = await Runner.RunAsync<NfceService, IReadOnlyList<NfceInfo>>(s => s.ListRecentAsync(100, default));
        var selected = SelectedNfce?.Info.DocumentId;
        NfceDocuments.Clear();
        foreach (var d in list) NfceDocuments.Add(new NfceRow(d));
        SelectedNfce = NfceDocuments.FirstOrDefault(r => r.Info.DocumentId == selected);
        var pending = list.Count(d => d.Status is FiscalDocumentStatus.Pending or FiscalDocumentStatus.Unknown or FiscalDocumentStatus.Contingency);
        var rejected = list.Count(d => d.Status == FiscalDocumentStatus.Rejected);
        NfceStatus = (pending, rejected) switch
        {
            (0, 0) => list.Count == 0 ? "Nenhuma NFC-e emitida ainda." : "Todas as NFC-e recentes estão resolvidas.",
            _ => $"{pending} pendente(s) de transmissão · {rejected} rejeitada(s) aguardando correção.",
        };
    }

    [RelayCommand]
    private void BrowseCertificate()
    {
        var file = Dialogs.PickFile("Certificado digital A1", "Certificado A1 (*.pfx;*.p12)|*.pfx;*.p12");
        if (file is null) return;
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length is 0 or > 100_000) { Dialogs.ShowError("Arquivo de certificado inválido."); return; }
        _pendingCertificate = bytes;
        CertificatePath = $"{Path.GetFileName(file)} (será enviado ao servidor ao salvar)";
        Dialogs.ShowInfo("Certificado carregado. Informe a senha e salve: o arquivo fica guardado no servidor da loja.");
    }

    [RelayCommand]
    private async Task SaveNfceSettingsAsync()
    {
        if (!CanManageCompany) return;
        if (!int.TryParse(NfceSeries, NumberStyles.None, CultureInfo.InvariantCulture, out var series) || series is < 0 or > 999)
        {
            Dialogs.ShowError("Série inválida (0 a 999).");
            return;
        }
        var environment = NfceEnvironment?.Value ?? FiscalEnvironment.Homologation;
        if (environment == FiscalEnvironment.Production && _nfceConfig?.Environment != FiscalEnvironment.Production
            && !Dialogs.Confirm("Em PRODUÇÃO as notas têm valor fiscal e geram obrigações para a empresa.\n\nConfirma a mudança para produção?"))
            return;

        var update = new NfceConfigUpdate(NfceEnabled, environment, series, _pendingCertificate,
            string.IsNullOrEmpty(CertificatePasswordInput) ? null : CertificatePasswordInput,
            string.IsNullOrWhiteSpace(CscId) ? null : CscId.Trim(),
            string.IsNullOrEmpty(CscTokenInput) ? null : CscTokenInput);
        var result = await Runner.RunAsync<NfceConfigurationService, Result<NfceConfigView>>(c => c.SaveAsync(ActorId, update, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        CertificatePasswordInput = "";
        CscTokenInput = "";
        Dialogs.ShowInfo(result.Value!.Enabled ? "Configuração da NFC-e salva no servidor. As próximas vendas de todos os caixas emitirão NFC-e."
            : "Configuração salva (NFC-e desativada).");
        await LoadNfceAsync();
    }

    [RelayCommand]
    private async Task TestSefazAsync()
    {
        try
        {
            var r = await Runner.RunAsync<NfceService, SefazResult>(s => s.ServiceStatusAsync());
            if (r.Outcome == SefazOutcome.Authorized) Dialogs.ShowInfo($"SEFAZ em operação.\n{r.StatusCode} - {r.Message}");
            else Dialogs.ShowError($"SEFAZ indisponível ou recusou a consulta:\n{r.StatusCode} - {r.Message}");
        }
        catch (Pdv.Domain.Common.DomainException ex)
        {
            Dialogs.ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task TransmitPendingNfceAsync()
    {
        var sent = await Runner.RunAsync<NfceService, int>(s => s.ProcessPendingAsync(take: 50));
        await LoadNfceDocumentsAsync();
        Dialogs.ShowInfo($"{sent} NFC-e autorizada(s) nesta transmissão.\n{NfceStatus}");
    }

    [RelayCommand]
    private async Task RetrySelectedNfceAsync()
    {
        if (SelectedNfce is not { } row) return;
        var result = await Runner.RunAsync<NfceService, Result<NfceInfo>>(s => s.IssueForSaleAsync(row.Info.SaleId));
        await LoadNfceDocumentsAsync();
        if (!result.Succeeded) Dialogs.ShowError(result.Error!);
        else Dialogs.ShowInfo($"NFC-e nº {result.Value!.Number}: {NfceRow.StatusLabel(result.Value.Status)}\n{result.Value.StatusCode} {result.Value.StatusMessage}");
    }

    [RelayCommand]
    private async Task ShowSelectedDanfeAsync()
    {
        if (SelectedNfce is not { } row) return;
        var receipt = await Runner.RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(row.Info.SaleId));
        if (receipt is not null) Dialogs.ShowReceipt(receipt, row.Info);
    }

    [RelayCommand]
    private async Task CancelSelectedNfceAsync()
    {
        if (SelectedNfce is not { Info.Status: FiscalDocumentStatus.Authorized } row)
        {
            Dialogs.ShowError("Selecione uma NFC-e autorizada.");
            return;
        }
        var reason = Dialogs.Prompt("Cancelar NFC-e", $"NFC-e nº {row.Info.Number} — {row.Info.Total:C}\nJustificativa (mínimo 15 caracteres, vai para a SEFAZ):");
        if (string.IsNullOrWhiteSpace(reason)) return;
        var result = await Runner.RunAsync<NfceService, Result<NfceInfo>>(s => s.CancelAsync(row.Info.SaleId, reason, ActorId));
        await LoadNfceDocumentsAsync();
        if (!result.Succeeded) Dialogs.ShowError(result.Error!);
        else Dialogs.ShowInfo("NFC-e cancelada na SEFAZ. Lembre de cancelar também a venda no caixa (F7) para estornar estoque e dinheiro.");
    }
}

public sealed record NfceRow(NfceInfo Info)
{
    public string Number => $"{Info.Number:000000000}";
    public string Series => $"{Info.Series:000}";
    public long SaleNumber => Info.SaleNumber;
    public string When => (Info.IssuedAt ?? Info.AuthorizedAt)?.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture) ?? "";
    public decimal Total => Info.Total;
    public string Status => StatusLabel(Info.Status);
    public string Detail => Info.StatusCode is { } code && code > 0 ? $"{code} - {Info.StatusMessage}" : Info.StatusMessage ?? "";
    public string Environment => Info.Environment == FiscalEnvironment.Homologation ? "Homologação" : "Produção";
    public bool IsOk => Info.Status == FiscalDocumentStatus.Authorized;
    public bool IsProblem => Info.Status is FiscalDocumentStatus.Rejected or FiscalDocumentStatus.Denied;

    public static string StatusLabel(FiscalDocumentStatus status) => status switch
    {
        FiscalDocumentStatus.Authorized => "Autorizada",
        FiscalDocumentStatus.Pending => "Pendente",
        FiscalDocumentStatus.Unknown => "Sem resposta",
        FiscalDocumentStatus.Contingency => "Contingência",
        FiscalDocumentStatus.Rejected => "Rejeitada",
        FiscalDocumentStatus.Denied => "Denegada",
        FiscalDocumentStatus.Cancelled => "Cancelada",
        _ => status.ToString(),
    };
}
