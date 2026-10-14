using Pdv.Domain.Common;

namespace Pdv.Domain.Stores;

public class Company : Entity
{
    private Company() { LegalName = null!; }

    public Company(string legalName, string? cnpj)
    {
        if (string.IsNullOrWhiteSpace(legalName)) throw new DomainException("Razão social é obrigatória.");
        LegalName = legalName.Trim();
        Cnpj = string.IsNullOrWhiteSpace(cnpj) ? null : new string(cnpj.Where(char.IsDigit).ToArray());
    }

    public string LegalName { get; private set; }
    public string? Cnpj { get; private set; }
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

    public Guid StoreId { get; private set; }
    public string Code { get; private set; }
    public string MachineName { get; private set; }
    public bool IsActive { get; private set; }
}
