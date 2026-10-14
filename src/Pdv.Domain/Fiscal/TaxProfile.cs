using System.Diagnostics.CodeAnalysis;
using Pdv.Domain.Common;

namespace Pdv.Domain.Fiscal;

/// <summary>
/// Perfil tributário reutilizável (ex.: "Revenda — Simples Nacional"): evita preencher CFOP e CST produto a produto.
/// Usa CSOSN (Simples Nacional/MEI) ou CST do ICMS (regime normal e Simples com excesso de sublimite), nunca os dois.
/// </summary>
public class TaxProfile : Entity
{
    private TaxProfile() { Name = Cfop = PisCst = CofinsCst = null!; }

    public TaxProfile(string name, string cfop, string? csosn, string? icmsCst, decimal icmsRate,
        string pisCst, decimal pisRate, string cofinsCst, decimal cofinsRate)
    {
        Update(name, cfop, csosn, icmsCst, icmsRate, pisCst, pisRate, cofinsCst, cofinsRate);
        IsActive = true;
    }

    public string Name { get; private set; }
    public string Cfop { get; private set; }

    /// <summary>Código de Situação da Operação no Simples Nacional.</summary>
    public string? Csosn { get; private set; }

    /// <summary>Código de Situação Tributária do ICMS (regime normal).</summary>
    public string? IcmsCst { get; private set; }

    public decimal IcmsRate { get; private set; }
    public string PisCst { get; private set; }
    public decimal PisRate { get; private set; }
    public string CofinsCst { get; private set; }
    public decimal CofinsRate { get; private set; }
    public bool IsActive { get; private set; }

    public bool UsesCsosn => Csosn is not null;

    [MemberNotNull(nameof(Name), nameof(Cfop), nameof(PisCst), nameof(CofinsCst))]
    public void Update(string name, string cfop, string? csosn, string? icmsCst, decimal icmsRate,
        string pisCst, decimal pisRate, string cofinsCst, decimal cofinsRate)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Nome do perfil tributário é obrigatório.");
        if (!FiscalCodes.IsValidNfceCfop(cfop)) throw new DomainException("CFOP inválido para NFC-e (4 dígitos começando com 5).");

        csosn = string.IsNullOrWhiteSpace(csosn) ? null : csosn.Trim();
        icmsCst = string.IsNullOrWhiteSpace(icmsCst) ? null : icmsCst.Trim();
        if ((csosn is null) == (icmsCst is null)) throw new DomainException("Informe CSOSN (Simples Nacional) ou CST do ICMS (regime normal).");
        if (csosn is not null && !FiscalCodes.Csosn.Contains(csosn)) throw new DomainException($"CSOSN {csosn} inválido.");
        if (icmsCst is not null && !FiscalCodes.IcmsCst.Contains(icmsCst)) throw new DomainException($"CST do ICMS {icmsCst} inválido.");
        if (!FiscalCodes.PisCofinsCst.Contains(pisCst?.Trim() ?? "")) throw new DomainException("CST do PIS inválido.");
        if (!FiscalCodes.PisCofinsCst.Contains(cofinsCst?.Trim() ?? "")) throw new DomainException("CST da COFINS inválido.");

        Name = name.Trim();
        Cfop = FiscalCodes.Digits(cfop);
        Csosn = csosn;
        IcmsCst = icmsCst;
        IcmsRate = FiscalCodes.RequireRate(icmsRate, "ICMS");
        PisCst = pisCst!.Trim();
        PisRate = FiscalCodes.RequireRate(pisRate, "PIS");
        CofinsCst = cofinsCst!.Trim();
        CofinsRate = FiscalCodes.RequireRate(cofinsRate, "COFINS");
    }

    /// <summary>O perfil precisa usar o código de ICMS compatível com o regime da empresa.</summary>
    public void EnsureCompatibleWith(TaxRegime regime)
    {
        if (regime.UsesCsosn() != UsesCsosn)
            throw new DomainException(regime.UsesCsosn()
                ? $"O perfil \"{Name}\" usa CST, mas a empresa é do Simples Nacional (use CSOSN)."
                : $"O perfil \"{Name}\" usa CSOSN, mas a empresa não é do Simples Nacional (use CST).");
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
