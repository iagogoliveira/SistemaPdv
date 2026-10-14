using System.Globalization;
using System.Security.Cryptography;
using Pdv.Domain.Common;

namespace Pdv.Domain.Fiscal;

public enum FiscalEnvironment
{
    Production = 1,
    Homologation = 2,
}

/// <summary>tpEmis da NF-e/NFC-e: 1 = normal; 9 = contingência offline da NFC-e.</summary>
public enum FiscalEmissionType
{
    Normal = 1,
    OfflineContingency = 9,
}

public enum FiscalDocumentStatus
{
    /// <summary>Número reservado e XML assinado, ainda não enviado (ou envio sem resposta).</summary>
    Pending,

    /// <summary>Envio feito sem resposta conclusiva (timeout): consultar a chave antes de reenviar.</summary>
    Unknown,

    /// <summary>Emitida offline (tpEmis 9) por indisponibilidade; precisa ser transmitida depois.</summary>
    Contingency,

    Authorized,
    Rejected,

    /// <summary>Uso denegado: o número fica consumido e a nota não tem validade.</summary>
    Denied,

    Cancelled,
}

/// <summary>
/// NFC-e (modelo 65) vinculada a uma venda. Separada da venda: a venda existe mesmo com a nota pendente,
/// rejeitada ou em contingência. Rejeição não consome o número; a correção reenvia com o mesmo número.
/// </summary>
public class FiscalDocument : Entity
{
    public const int NfceModel = 65;

    private FiscalDocument() { AccessKey = null!; }

    public FiscalDocument(Guid saleId, Guid storeId, FiscalEnvironment environment, int series, long number, DateTimeOffset createdAt)
    {
        if (series is < 0 or > 999) throw new DomainException("Série inválida.");
        if (number is < 1 or > 999_999_999) throw new DomainException("Número da NFC-e inválido.");
        SaleId = saleId;
        StoreId = storeId;
        Environment = environment;
        Series = series;
        Number = number;
        CreatedAt = createdAt;
        Status = FiscalDocumentStatus.Pending;
        EmissionType = FiscalEmissionType.Normal;
        AccessKey = "";
    }

    public Guid SaleId { get; private set; }
    public Guid StoreId { get; private set; }
    public int Model { get; private set; } = NfceModel;
    public FiscalEnvironment Environment { get; private set; }
    public int Series { get; private set; }
    public long Number { get; private set; }
    public FiscalEmissionType EmissionType { get; private set; }
    public FiscalDocumentStatus Status { get; private set; }
    public string AccessKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? IssuedAt { get; private set; }
    public DateTimeOffset? AuthorizedAt { get; private set; }
    public string? Protocol { get; private set; }
    public int? StatusCode { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? ContingencyReason { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>XML assinado enviado (ou a enviar) à SEFAZ.</summary>
    public string? SignedXml { get; private set; }

    /// <summary>nfeProc: XML assinado + protocolo de autorização. É o arquivo a guardar por 5 anos.</summary>
    public string? AuthorizedXml { get; private set; }

    public string? QrCodeUrl { get; private set; }
    public string? ConsultationUrl { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancellationProtocol { get; private set; }
    public string? CancellationReason { get; private set; }

    public bool IsFinal => Status is FiscalDocumentStatus.Authorized or FiscalDocumentStatus.Denied or FiscalDocumentStatus.Cancelled;

    /// <summary>Notas que ainda precisam ir (ou voltar) à SEFAZ.</summary>
    public bool NeedsTransmission => Status is FiscalDocumentStatus.Pending or FiscalDocumentStatus.Unknown or FiscalDocumentStatus.Contingency;

    /// <summary>Registra o XML assinado (nova chave a cada montagem: tpEmis e data fazem parte dela).</summary>
    public void SetSignedXml(string accessKey, FiscalEmissionType emissionType, DateTimeOffset issuedAt, string signedXml,
        string qrCodeUrl, string consultationUrl, string? contingencyReason = null)
    {
        if (IsFinal) throw new DomainException("A nota já foi concluída.");
        if (!Fiscal.AccessKey.IsValid(accessKey)) throw new DomainException("Chave de acesso inválida.");
        AccessKey = accessKey;
        EmissionType = emissionType;
        IssuedAt = issuedAt;
        SignedXml = signedXml;
        QrCodeUrl = qrCodeUrl;
        ConsultationUrl = consultationUrl;
        if (emissionType == FiscalEmissionType.OfflineContingency)
        {
            ContingencyReason = string.IsNullOrWhiteSpace(contingencyReason) ? "SEFAZ indisponível" : contingencyReason.Trim();
            Status = FiscalDocumentStatus.Contingency;
        }
        else if (Status != FiscalDocumentStatus.Rejected)
        {
            Status = FiscalDocumentStatus.Pending;
        }
    }

    public void RegisterAttempt(DateTimeOffset at)
    {
        Attempts++;
        LastAttemptAt = at;
    }

    public void MarkAuthorized(string protocol, int statusCode, string message, string authorizedXml, DateTimeOffset authorizedAt)
    {
        if (Status is FiscalDocumentStatus.Cancelled or FiscalDocumentStatus.Denied) throw new DomainException("A nota não pode ser autorizada.");
        Status = FiscalDocumentStatus.Authorized;
        Protocol = protocol;
        StatusCode = statusCode;
        StatusMessage = message;
        AuthorizedXml = authorizedXml;
        AuthorizedAt = authorizedAt;
    }

    public void MarkRejected(int statusCode, string message)
    {
        if (IsFinal) throw new DomainException("A nota já foi concluída.");
        Status = FiscalDocumentStatus.Rejected;
        StatusCode = statusCode;
        StatusMessage = message;
    }

    public void MarkDenied(int statusCode, string message, string? protocol)
    {
        if (IsFinal) throw new DomainException("A nota já foi concluída.");
        Status = FiscalDocumentStatus.Denied;
        StatusCode = statusCode;
        StatusMessage = message;
        Protocol = protocol;
    }

    /// <summary>Envio sem resposta conclusiva: a próxima tentativa consulta a chave antes de reenviar.</summary>
    public void MarkUnknown(string message)
    {
        if (IsFinal) throw new DomainException("A nota já foi concluída.");
        if (Status != FiscalDocumentStatus.Contingency) Status = FiscalDocumentStatus.Unknown;
        StatusMessage = message;
    }

    public void MarkCancelled(string protocol, string reason, DateTimeOffset at)
    {
        if (Status != FiscalDocumentStatus.Authorized) throw new DomainException("Só é possível cancelar NFC-e autorizada.");
        Status = FiscalDocumentStatus.Cancelled;
        CancellationProtocol = protocol;
        CancellationReason = reason;
        CancelledAt = at;
    }

    /// <summary>Justificativa de cancelamento exigida pela SEFAZ: 15 a 255 caracteres.</summary>
    public static string ValidateJustification(string? reason)
    {
        var text = (reason ?? "").Trim();
        if (text.Length is < 15 or > 255) throw new DomainException("A justificativa deve ter entre 15 e 255 caracteres.");
        return text;
    }
}

/// <summary>Próximo número por loja, série e ambiente (homologação e produção têm numeração própria).</summary>
public class FiscalSeries : Entity
{
    private FiscalSeries() { }

    public FiscalSeries(Guid storeId, FiscalEnvironment environment, int series)
    {
        if (series is < 0 or > 999) throw new DomainException("Série inválida.");
        StoreId = storeId;
        Environment = environment;
        Series = series;
        NextNumber = 1;
    }

    public Guid StoreId { get; private set; }
    public FiscalEnvironment Environment { get; private set; }
    public int Model { get; private set; } = FiscalDocument.NfceModel;
    public int Series { get; private set; }
    public long NextNumber { get; private set; }

    /// <summary>Reserva o próximo número. A gravação usa controle de concorrência: dois caixas não pegam o mesmo.</summary>
    public long Reserve()
    {
        if (NextNumber > 999_999_999) throw new DomainException("Numeração da série esgotada.");
        return NextNumber++;
    }
}

/// <summary>
/// Chave de acesso de 44 posições: cUF(2) AAMM(4) CNPJ(14) mod(2) série(3) nNF(9) tpEmis(1) cNF(8) cDV(1).
/// Com o CNPJ alfanumérico, o dígito verificador usa o valor ASCII − 48 de cada caractere (como no CNPJ).
/// </summary>
public static class AccessKey
{
    private static readonly Dictionary<string, int> UfCodes = new()
    {
        ["RO"] = 11, ["AC"] = 12, ["AM"] = 13, ["RR"] = 14, ["PA"] = 15, ["AP"] = 16, ["TO"] = 17,
        ["MA"] = 21, ["PI"] = 22, ["CE"] = 23, ["RN"] = 24, ["PB"] = 25, ["PE"] = 26, ["AL"] = 27, ["SE"] = 28, ["BA"] = 29,
        ["MG"] = 31, ["ES"] = 32, ["RJ"] = 33, ["SP"] = 35, ["PR"] = 41, ["SC"] = 42, ["RS"] = 43,
        ["MS"] = 50, ["MT"] = 51, ["GO"] = 52, ["DF"] = 53,
    };

    public static int UfCode(string uf) =>
        UfCodes.TryGetValue(uf.ToUpperInvariant(), out var code) ? code : throw new DomainException($"UF {uf} inválida.");

    public static string Build(string uf, DateTimeOffset issuedAt, string cnpj, int model, int series, long number,
        FiscalEmissionType emissionType, int randomCode)
    {
        var normalizedCnpj = TaxDocuments.Normalize(cnpj);
        if (normalizedCnpj.Length != 14) throw new DomainException("CNPJ do emitente inválido para a chave de acesso.");
        if (randomCode is < 0 or > 99_999_999) throw new DomainException("Código numérico da chave inválido.");

        var body = string.Concat(
            UfCode(uf).ToString("00", CultureInfo.InvariantCulture),
            issuedAt.ToString("yyMM", CultureInfo.InvariantCulture),
            normalizedCnpj,
            model.ToString("00", CultureInfo.InvariantCulture),
            series.ToString("000", CultureInfo.InvariantCulture),
            number.ToString("000000000", CultureInfo.InvariantCulture),
            ((int)emissionType).ToString(CultureInfo.InvariantCulture),
            randomCode.ToString("00000000", CultureInfo.InvariantCulture));
        return body + CheckDigit(body);
    }

    /// <summary>cNF aleatório de 8 dígitos, diferente do número da nota (exigência do leiaute).</summary>
    public static int NewRandomCode(long number)
    {
        int code;
        do code = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000);
        while (code == number % 100_000_000);
        return code;
    }

    /// <summary>cUF+AAMM numéricos, CNPJ (posições 7–20) numérico ou alfanumérico, restante numérico, DV conferido.</summary>
    public static bool IsValid(string? key) =>
        key is { Length: 44 } k && k[..6].All(char.IsAsciiDigit) && k[6..20].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c))
        && k[20..].All(char.IsAsciiDigit) && CheckDigit(k[..43]) == k[43] - '0';

    public static int CheckDigit(string body43) => TaxDocuments.CnpjCheckDigit(body43);

    /// <summary>Formato do DANFE: 11 grupos de 4 caracteres.</summary>
    public static string Format(string key) => string.Join(' ', Enumerable.Range(0, 11).Select(i => key.Substring(i * 4, 4)));
}
