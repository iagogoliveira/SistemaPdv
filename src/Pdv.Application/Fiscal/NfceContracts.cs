using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;

namespace Pdv.Application.Fiscal;

/// <summary>Configuração de emissão da máquina (segredos ficam protegidos fora do banco).</summary>
public sealed record NfceSettings(
    bool Enabled,
    FiscalEnvironment Environment,
    int Series,
    string? CertificatePath,
    string? CertificatePassword,
    string? CscId,
    string? CscToken,
    int TimeoutSeconds = 30,
    int CancellationWindowMinutes = 30)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(CertificatePath) && !string.IsNullOrWhiteSpace(CscId) && !string.IsNullOrWhiteSpace(CscToken);
}

public interface INfceSettingsStore
{
    NfceSettings Load();
    void Save(NfceSettings settings);
}

/// <summary>Tudo o que é preciso para montar o XML: vem do retrato da venda e dos dados do emitente.</summary>
public sealed record NfceDraft(
    FiscalEnvironment Environment,
    FiscalEmissionType EmissionType,
    string AccessKey,
    int RandomCode,
    int Series,
    long Number,
    DateTimeOffset IssuedAt,
    string? ContingencyReason,
    NfceEmitter Emitter,
    string? CustomerDocument,
    string? CustomerName,
    IReadOnlyList<NfceItem> Items,
    IReadOnlyList<NfcePayment> Payments,
    decimal Change,
    string? AdditionalInfo);

public sealed record NfceEmitter(
    string Cnpj, string LegalName, string? TradeName, string StateRegistration, TaxRegime TaxRegime,
    string Street, string Number, string? Complement, string District, string CityIbgeCode, string City, string Uf, string ZipCode, string? Phone);

public sealed record NfceItem(
    int LineNumber, string ProductCode, string? Gtin, string Description, string Ncm, string? Cest, string Cfop, string Unit,
    decimal Quantity, decimal UnitPrice, decimal GrossTotal, decimal Discount,
    ProductOrigin Origin, string? Csosn, string? IcmsCst, decimal IcmsRate,
    string PisCst, decimal PisRate, string CofinsCst, decimal CofinsRate);

public sealed record NfcePayment(PaymentMethod Method, decimal Amount);

/// <summary>XML assinado pronto para envio.</summary>
public sealed record SignedNfce(string Xml, string QrCodeUrl, string ConsultationUrl);

public enum SefazOutcome
{
    Authorized,
    Rejected,
    Denied,

    /// <summary>SEFAZ fora do ar / sem conexão: emitir em contingência.</summary>
    Unavailable,

    /// <summary>Enviado, mas sem resposta conclusiva: consultar pela chave.</summary>
    Unknown,

    /// <summary>Consulta de chave: a SEFAZ não conhece a nota (pode reenviar com segurança).</summary>
    NotFound,
}

public sealed record SefazResult(SefazOutcome Outcome, int StatusCode, string Message, string? Protocol = null,
    string? AuthorizedXml = null, DateTimeOffset? ProcessedAt = null);

/// <summary>
/// Porta para a SEFAZ. A implementação real (DFe.NET) fica no projeto Pdv.Fiscal; os testes usam uma SEFAZ simulada.
/// </summary>
public interface INfceGateway
{
    SignedNfce BuildAndSign(NfceDraft draft, NfceSettings settings);

    Task<SefazResult> AuthorizeAsync(string signedXml, NfceSettings settings, string uf, CancellationToken ct);

    Task<SefazResult> QueryAsync(string accessKey, NfceSettings settings, string uf, CancellationToken ct);

    Task<SefazResult> CancelAsync(string accessKey, string protocol, string cnpj, string justification, NfceSettings settings,
        string uf, CancellationToken ct);

    Task<SefazResult> ServiceStatusAsync(NfceSettings settings, string uf, CancellationToken ct);
}
