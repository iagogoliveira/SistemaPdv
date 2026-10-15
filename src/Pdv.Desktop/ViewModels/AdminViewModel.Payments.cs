using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Payments;
using Pdv.Domain.Payments;

namespace Pdv.Desktop.ViewModels;

/// <summary>Retaguarda — aba Pagamentos: Pix integrado (configuração e conciliação) e TEF deste caixa.</summary>
public partial class AdminViewModel
{
    public bool ShowPaymentsTab => CanManageCompany || CanViewReports;

    public IReadOnlyList<Choice<string>> PixProviders { get; } =
    [
        new(PixSettings.BacenApi, "API Pix do Banco Central (banco/PSP da loja)"),
        new(PixSettings.Simulated, "Simulado (treinamento, sem valor)"),
    ];

    public ObservableCollection<PixRow> PixCharges { get; } = [];

    [ObservableProperty] private PixRow? _selectedPix;
    [ObservableProperty] private bool _pixEnabled;
    [ObservableProperty] private Choice<string>? _pixProvider;
    [ObservableProperty] private string _pixBaseUrl = "";
    [ObservableProperty] private string _pixTokenUrl = "";
    [ObservableProperty] private string _pixClientId = "";
    [ObservableProperty] private string _pixKey = "";
    [ObservableProperty] private string _pixPspCnpj = "";
    [ObservableProperty] private string _pixPspName = "";
    [ObservableProperty] private string _pixExpiration = "300";
    [ObservableProperty] private string? _pixCertificateText;
    [ObservableProperty] private bool _hasPixSecret;
    [ObservableProperty] private string _pixSummary = "";
    [ObservableProperty] private string _tefStatus = "";

    /// <summary>Preenchidos pela janela (PasswordBox). Vazio = manter.</summary>
    public string PixClientSecretInput { get; set; } = "";
    public string PixCertificatePasswordInput { get; set; } = "";

    private byte[]? _pendingPixCertificate;

    public void AttachPaymentDevices(PaymentDevices devices) =>
        TefStatus = devices.Tef is { } tef
            ? $"TEF deste caixa: {tef.Name}. Cartões de crédito e débito são cobrados no pinpad e saem na NFC-e com NSU, bandeira e autorização."
            : "TEF não configurado neste caixa: cartões são lançados como maquininha não integrada (Pdv:Tef:Provider no appsettings da máquina).";

    private async Task LoadPaymentsAsync()
    {
        if (CanManageCompany)
        {
            var c = await Runner.RunAsync<PixConfigurationService, PixConfigView>(s => s.GetAsync(ActorId, default));
            PixEnabled = c.Enabled;
            PixProvider = PixProviders.FirstOrDefault(p => p.Value == c.Provider) ?? PixProviders[0];
            PixBaseUrl = c.BaseUrl ?? "";
            PixTokenUrl = c.TokenUrl ?? "";
            PixClientId = c.ClientId ?? "";
            PixKey = c.PixKey ?? "";
            PixPspCnpj = c.PspCnpj ?? "";
            PixPspName = c.PspName ?? "";
            PixExpiration = c.ExpirationSeconds.ToString(CultureInfo.InvariantCulture);
            HasPixSecret = c.HasClientSecret;
            _pendingPixCertificate = null;
            PixCertificateText = c.HasCertificate ? "Certificado do PSP instalado no servidor" : null;
        }
        if (CanViewReports) await LoadPixChargesAsync();
    }

    private async Task LoadPixChargesAsync()
    {
        var to = DateOnly.FromDateTime(DateTime.Today);
        var rows = await Runner.RunAsync<PixService, IReadOnlyList<PixChargeRow>>(s => s.ListAsync(ActorId, to.AddDays(-30), to, default));
        var selected = SelectedPix?.Row.Id;
        PixCharges.Clear();
        foreach (var r in rows) PixCharges.Add(new PixRow(r));
        SelectedPix = PixCharges.FirstOrDefault(r => r.Row.Id == selected);
        var toRefund = rows.Where(r => r.CanRefund).ToList();
        PixSummary = rows.Count == 0 ? "Nenhum Pix integrado nos últimos 30 dias."
            : $"{rows.Count(r => r.Status == PixChargeStatus.Paid)} pago(s) · {rows.Where(r => r.Status == PixChargeStatus.Paid).Sum(r => r.Amount):C}" +
              (toRefund.Count > 0 ? $" · ATENÇÃO: {toRefund.Count} pago(s) sem venda ({toRefund.Sum(r => r.Amount):C}) — usar ou devolver" : "");
    }

    [RelayCommand]
    private void BrowsePixCertificate()
    {
        var file = Dialogs.PickFile("Certificado do PSP (Pix)", "Certificado (*.pfx;*.p12)|*.pfx;*.p12");
        if (file is null) return;
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length is 0 or > 100_000) { Dialogs.ShowError("Arquivo de certificado inválido."); return; }
        _pendingPixCertificate = bytes;
        PixCertificateText = $"{Path.GetFileName(file)} (será enviado ao servidor ao salvar)";
    }

    [RelayCommand]
    private async Task SavePixSettingsAsync()
    {
        if (!int.TryParse(PixExpiration, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            Dialogs.ShowError("Validade do QR Code inválida (segundos).");
            return;
        }
        var update = new PixConfigUpdate(PixEnabled, PixProvider?.Value ?? PixSettings.BacenApi, PixBaseUrl, PixTokenUrl, PixClientId,
            string.IsNullOrEmpty(PixClientSecretInput) ? null : PixClientSecretInput, _pendingPixCertificate,
            string.IsNullOrEmpty(PixCertificatePasswordInput) ? null : PixCertificatePasswordInput, PixKey, PixPspCnpj, PixPspName, seconds);
        var result = await Runner.RunAsync<PixConfigurationService, Result<PixConfigView>>(s => s.SaveAsync(ActorId, update, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        PixClientSecretInput = "";
        PixCertificatePasswordInput = "";
        Dialogs.ShowInfo(result.Value!.Enabled
            ? "Pix integrado ativado. Nos caixas, a forma Pix passa a gerar QR Code dinâmico com confirmação automática."
            : "Configuração salva (Pix integrado desativado: Pix volta a ser conferido pelo operador).");
        await LoadPaymentsAsync();
    }

    [RelayCommand]
    private async Task RefreshSelectedPixAsync()
    {
        if (SelectedPix is not { } row) { Dialogs.ShowError("Selecione um Pix na lista."); return; }
        var id = row.Row.Id;
        var view = await Runner.RunAsync<PixService, PixChargeView>(s => s.RefreshAsync(ActorId, id, default));
        await LoadPixChargesAsync();
        Dialogs.ShowInfo($"Pix {view.Amount:C}: {PixRow.Label(view.Status)}{(view.Message is { } m ? $"\n{m}" : "")}");
    }

    [RelayCommand]
    private async Task RefundSelectedPixAsync()
    {
        if (SelectedPix is not { Row.CanRefund: true } row)
        {
            Dialogs.ShowError("Selecione um Pix pago sem venda concluída (ou de venda cancelada).");
            return;
        }
        var reason = Dialogs.Prompt("Devolver Pix", $"Devolver {row.Row.Amount:C} para {row.Row.PayerName ?? "o pagador"}.\nMotivo:");
        if (string.IsNullOrWhiteSpace(reason)) return;
        var id = row.Row.Id;
        var result = await Runner.RunAsync<PixService, Result<PixChargeView>>(s => s.RefundAsync(ActorId, id, reason, default));
        await LoadPixChargesAsync();
        if (!result.Succeeded) Dialogs.ShowError(result.Error!);
        else Dialogs.ShowInfo(result.Value!.Message ?? "Devolução solicitada ao PSP.");
    }

    public sealed record PixRow(PixChargeRow Row)
    {
        public string When => Row.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);
        public string Terminal => Row.TerminalCode;
        public decimal Amount => Row.Amount;
        public string Status => Label(Row.Status) + (Row.CanRefund ? " — devolver" : "");
        public string Sale => Row.SaleNumber is { } n ? $"{n}{(Row.SaleCancelled ? " (cancelada)" : "")}" : "—";
        public string Payer => Row.PayerName ?? "";
        public bool NeedsAttention => Row.CanRefund;

        public static string Label(PixChargeStatus status) => status switch
        {
            PixChargeStatus.Active => "Aguardando",
            PixChargeStatus.Paid => "Pago",
            PixChargeStatus.Expired => "Expirado",
            PixChargeStatus.Cancelled => "Cancelado",
            PixChargeStatus.Refunded => "Devolvido",
            _ => status.ToString(),
        };
    }
}
