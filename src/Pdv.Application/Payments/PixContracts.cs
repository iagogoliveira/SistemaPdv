using Pdv.Domain.Payments;

namespace Pdv.Application.Payments;

/// <summary>
/// Configuração do Pix da loja, guardada no servidor (segredos cifrados). <see cref="BaseUrl"/> é a raiz da API Pix do PSP
/// até antes de "/cob" (ex.: https://pix.api.efipay.com.br/v2).
/// </summary>
public sealed record PixSettings(
    bool Enabled,
    string Provider,
    string? BaseUrl,
    string? TokenUrl,
    string? ClientId,
    string? ClientSecret,
    string? CertificatePath,
    string? CertificatePassword,
    string? PixKey,
    string? PspCnpj,
    string? PspName,
    int ExpirationSeconds = 300,
    int SimulatorPaysAfterSeconds = 8)
{
    public const string Simulated = "Simulado";
    public const string BacenApi = "ApiPix";

    public static PixSettings Disabled { get; } = new(false, Simulated, null, null, null, null, null, null, null, null, null);

    public bool IsComplete => Provider == Simulated
        || (!string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
            && !string.IsNullOrWhiteSpace(PixKey));
}

public interface IPixSettingsStore
{
    PixSettings Load();
    void Save(PixSettings settings);

    /// <summary>Grava o certificado do PSP (mTLS) na pasta do servidor e devolve o caminho.</summary>
    string ImportCertificate(byte[] pfx);
}

public sealed record PspChargeRequest(string Txid, decimal Amount, int ExpirationSeconds, string? Description);

public sealed record PspChargeCreated(string CopyPaste, DateTimeOffset ExpiresAt);

public enum PspChargeStatus
{
    Active,
    Paid,
    Removed,
}

public sealed record PspChargeState(PspChargeStatus Status, string? EndToEndId, decimal? PaidAmount, string? PayerName, DateTimeOffset? PaidAt);

public enum PspRefundStatus
{
    Processing,
    Refunded,
    Failed,
}

/// <summary>Erro de comunicação ou recusa do PSP (mensagem pronta para o operador).</summary>
public sealed class PixProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Porta para o PSP. A implementação padrão segue a API Pix do Banco Central (cob, devolução).</summary>
public interface IPixProvider
{
    string Name { get; }

    Task<PspChargeCreated> CreateAsync(PspChargeRequest request, PixSettings settings, CancellationToken ct);

    Task<PspChargeState> GetAsync(string txid, PixSettings settings, CancellationToken ct);

    Task CancelAsync(string txid, PixSettings settings, CancellationToken ct);

    Task<PspRefundStatus> RefundAsync(string endToEndId, string refundId, decimal amount, PixSettings settings, CancellationToken ct);
}

/// <summary>Cobrança como o caixa vê.</summary>
public sealed record PixChargeView(Guid Id, string Txid, decimal Amount, string CopyPaste, PixChargeStatus Status,
    DateTimeOffset ExpiresAt, DateTimeOffset? PaidAt, string? EndToEndId, string? PayerName, string? Message)
{
    public static PixChargeView From(PixCharge c) =>
        new(c.Id, c.Txid, c.Amount, c.CopyPaste, c.Status, c.ExpiresAt, c.PaidAt, c.EndToEndId, c.PayerName, c.Message);
}

/// <summary>Linha da conciliação de Pix na retaguarda.</summary>
public sealed record PixChargeRow(Guid Id, DateTimeOffset CreatedAt, string TerminalCode, decimal Amount, PixChargeStatus Status,
    long? SaleNumber, bool SaleCancelled, string? EndToEndId, string? PayerName, string? Message)
{
    /// <summary>Pago sem venda concluída (caixa desligou, venda desistida) ou com a venda cancelada: o dinheiro deve voltar.</summary>
    public bool CanRefund => Status == PixChargeStatus.Paid && (SaleNumber is null || SaleCancelled);
}

/// <summary>Configuração como a retaguarda vê (sem segredos).</summary>
public sealed record PixConfigView(bool Enabled, string Provider, string? BaseUrl, string? TokenUrl, string? ClientId, bool HasClientSecret,
    bool HasCertificate, string? PixKey, string? PspCnpj, string? PspName, int ExpirationSeconds);

/// <summary>Alteração da configuração. Segredos nulos ou vazios mantêm o valor salvo.</summary>
public sealed record PixConfigUpdate(bool Enabled, string Provider, string? BaseUrl, string? TokenUrl, string? ClientId, string? ClientSecret,
    byte[]? CertificatePfx, string? CertificatePassword, string? PixKey, string? PspCnpj, string? PspName, int ExpirationSeconds);
