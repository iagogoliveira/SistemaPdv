using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;

namespace Pdv.Domain.Stores;

/// <summary>Endereço do emitente, como exigido no grupo "enderEmit" da NF-e/NFC-e.</summary>
public sealed record Address(string Street, string Number, string? Complement, string District,
    string City, string CityIbgeCode, string Uf, string ZipCode)
{
    public static Address Create(string street, string number, string? complement, string district,
        string city, string cityIbgeCode, string uf, string zipCode)
    {
        if (string.IsNullOrWhiteSpace(street) || string.IsNullOrWhiteSpace(district) || string.IsNullOrWhiteSpace(city))
            throw new DomainException("Logradouro, bairro e município são obrigatórios.");
        var ibge = FiscalCodes.Digits(cityIbgeCode);
        if (ibge.Length != 7) throw new DomainException("Código IBGE do município deve ter 7 dígitos.");
        var normalizedUf = (uf ?? "").Trim().ToUpperInvariant();
        if (!FiscalCodes.Ufs.Contains(normalizedUf)) throw new DomainException("UF inválida.");
        var cep = FiscalCodes.Digits(zipCode);
        if (cep.Length != 8) throw new DomainException("CEP deve ter 8 dígitos.");
        return new Address(street.Trim(), string.IsNullOrWhiteSpace(number) ? "S/N" : number.Trim(),
            string.IsNullOrWhiteSpace(complement) ? null : complement.Trim(), district.Trim(), city.Trim(), ibge, normalizedUf, cep);
    }
}

/// <summary>Empresa emitente. Os dados fiscais podem ficar pendentes até o primeiro cadastro completo.</summary>
public class Company : Entity
{
    private Company() { LegalName = null!; }

    public Company(string legalName, string? cnpj)
    {
        if (string.IsNullOrWhiteSpace(legalName)) throw new DomainException("Razão social é obrigatória.");
        LegalName = legalName.Trim();
        if (!string.IsNullOrWhiteSpace(cnpj))
        {
            if (!TaxDocuments.IsValidCnpj(cnpj)) throw new DomainException("CNPJ inválido.");
            Cnpj = TaxDocuments.Normalize(cnpj);
        }
        TaxRegime = TaxRegime.SimplesNacional;
    }

    public string LegalName { get; private set; }
    public string? TradeName { get; private set; }

    /// <summary>14 posições, numérico ou alfanumérico, sem pontuação.</summary>
    public string? Cnpj { get; private set; }

    /// <summary>Inscrição estadual (somente dígitos) ou "ISENTO".</summary>
    public string? StateRegistration { get; private set; }

    public string? MunicipalRegistration { get; private set; }
    public TaxRegime TaxRegime { get; private set; }
    public Address? Address { get; private set; }
    public string? Phone { get; private set; }

    /// <summary>Perfil usado pelos produtos que não têm perfil próprio.</summary>
    public Guid? DefaultTaxProfileId { get; private set; }

    public bool IsFiscallyConfigured =>
        Cnpj is not null && StateRegistration is not null && Address is not null && DefaultTaxProfileId is not null;

    public void UpdateRegistration(string legalName, string? tradeName, string cnpj, string stateRegistration,
        string? municipalRegistration, TaxRegime regime, Address address, string? phone)
    {
        if (string.IsNullOrWhiteSpace(legalName)) throw new DomainException("Razão social é obrigatória.");
        if (!TaxDocuments.IsValidCnpj(cnpj)) throw new DomainException("CNPJ inválido.");
        if (!Enum.IsDefined(regime)) throw new DomainException("Regime tributário inválido.");

        var ie = (stateRegistration ?? "").Trim().ToUpperInvariant();
        if (ie != "ISENTO")
        {
            ie = FiscalCodes.Digits(ie);
            if (ie.Length is < 2 or > 14) throw new DomainException("Inscrição estadual inválida (somente números, ou ISENTO).");
        }

        LegalName = legalName.Trim();
        TradeName = string.IsNullOrWhiteSpace(tradeName) ? null : tradeName.Trim();
        Cnpj = TaxDocuments.Normalize(cnpj);
        StateRegistration = ie;
        MunicipalRegistration = string.IsNullOrWhiteSpace(municipalRegistration) ? null : municipalRegistration.Trim();
        TaxRegime = regime;
        Address = address;
        Phone = string.IsNullOrWhiteSpace(phone) ? null : FiscalCodes.Digits(phone);
    }

    public void SetDefaultTaxProfile(TaxProfile profile)
    {
        if (!profile.IsActive) throw new DomainException("O perfil tributário padrão precisa estar ativo.");
        profile.EnsureCompatibleWith(TaxRegime);
        DefaultTaxProfileId = profile.Id;
    }
}

public class Store : Entity
{
    private Store() { Name = null!; }

    public Store(Guid companyId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Nome da loja é obrigatório.");
        CompanyId = companyId;
        Name = name.Trim();
    }

    public Guid CompanyId { get; private set; }
    public string Name { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Nome da loja é obrigatório.");
        Name = name.Trim();
    }
}

/// <summary>Computador/caixa autorizado a operar na loja.</summary>
public class Terminal : Entity
{
    private Terminal() { Code = MachineName = null!; }

    public Terminal(Guid storeId, string code, string machineName)
    {
        StoreId = storeId;
        Code = code;
        MachineName = machineName;
        IsActive = true;
    }

    /// <summary>Recria um caixa com o mesmo ID (restauração de backup anterior ao cadastro do caixa).</summary>
    public static Terminal Recreate(Guid id, Guid storeId, string code, string machineName) =>
        new(storeId, code, machineName) { Id = id };

    public Guid StoreId { get; private set; }
    public string Code { get; private set; }
    public string MachineName { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Hash (SHA-256) da chave de acesso do caixa ao servidor; a chave em si fica só no caixa.</summary>
    public string? AccessKeyHash { get; private set; }
    public DateTimeOffset? PairedAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    public string? AppVersion { get; private set; }

    /// <summary>Conecta (ou reconecta) o caixa ao servidor com uma nova chave; a anterior deixa de valer.</summary>
    public void Pair(string accessKeyHash, DateTimeOffset at)
    {
        AccessKeyHash = accessKeyHash;
        PairedAt = at;
        IsActive = true;
    }

    public void Revoke()
    {
        AccessKeyHash = null;
        IsActive = false;
    }

    public void Seen(DateTimeOffset at, string? appVersion)
    {
        LastSeenAt = at;
        AppVersion = appVersion;
    }
}
