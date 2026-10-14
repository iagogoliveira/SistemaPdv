using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Stores;

namespace Pdv.Application.Fiscal;

public sealed record CompanySettingsModel(
    string LegalName, string? TradeName, string? Cnpj, string? StateRegistration, string? MunicipalRegistration,
    TaxRegime TaxRegime, string? Street, string? Number, string? Complement, string? District, string? City,
    string? CityIbgeCode, string? Uf, string? ZipCode, string? Phone, Guid? DefaultTaxProfileId, string StoreName);

public sealed record FiscalReadiness(bool CompanyConfigured, int ActiveProducts, int ProductsWithoutNcm)
{
    public bool IsReady => CompanyConfigured && ProductsWithoutNcm == 0;
}

public sealed record TaxProfileModel(Guid? Id, string Name, string Cfop, string? Csosn, string? IcmsCst, decimal IcmsRate,
    string PisCst, decimal PisRate, string CofinsCst, decimal CofinsRate, bool IsActive = true, bool IsDefault = false)
{
    public string IcmsCode => Csosn is not null ? $"CSOSN {Csosn}" : $"CST {IcmsCst}";
}

/// <summary>Dados do emitente (empresa e loja) usados na NFC-e.</summary>
public sealed class CompanySettingsService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    public async Task<CompanySettingsModel> GetAsync(CancellationToken ct = default)
    {
        var (company, store) = await LoadAsync(ct);
        var a = company.Address;
        return new CompanySettingsModel(company.LegalName, company.TradeName, company.Cnpj, company.StateRegistration,
            company.MunicipalRegistration, company.TaxRegime, a?.Street, a?.Number, a?.Complement, a?.District, a?.City,
            a?.CityIbgeCode, a?.Uf, a?.ZipCode, company.Phone, company.DefaultTaxProfileId, store.Name);
    }

    public async Task<Result<Guid>> SaveAsync(Guid actorId, CompanySettingsModel m, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageCompany, ct))
            return Result<Guid>.Fail("Usuário sem permissão para alterar os dados da empresa.");
        try
        {
            var (company, store) = await LoadAsync(ct);
            var address = Address.Create(m.Street ?? "", m.Number ?? "", m.Complement, m.District ?? "", m.City ?? "",
                m.CityIbgeCode ?? "", m.Uf ?? "", m.ZipCode ?? "");
            company.UpdateRegistration(m.LegalName, m.TradeName, m.Cnpj ?? "", m.StateRegistration ?? "",
                m.MunicipalRegistration, m.TaxRegime, address, m.Phone);
            store.Rename(m.StoreName);

            var defaultId = m.DefaultTaxProfileId ?? company.DefaultTaxProfileId;
            if (defaultId is { } id)
            {
                var profile = await db.TaxProfiles.SingleOrDefaultAsync(p => p.Id == id, ct)
                    ?? throw new DomainException("Perfil tributário padrão não encontrado.");
                company.SetDefaultTaxProfile(profile);
            }

            db.AuditEntries.Add(new AuditEntry("company.updated", actorId, null, company.Id,
                $"{company.LegalName} {company.Cnpj} CRT {(int)company.TaxRegime}", clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Ok(company.Id);
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail(ex.Message);
        }
    }

    public async Task<FiscalReadiness> GetReadinessAsync(CancellationToken ct = default)
    {
        var (company, _) = await LoadAsync(ct);
        var active = await db.Products.CountAsync(p => p.IsActive, ct);
        var withoutNcm = await db.Products.CountAsync(p => p.IsActive && p.Ncm == null, ct);
        return new FiscalReadiness(company.IsFiscallyConfigured, active, withoutNcm);
    }

    private async Task<(Company Company, Store Store)> LoadAsync(CancellationToken ct)
    {
        var store = await db.Stores.OrderBy(s => s.Id).FirstAsync(ct);
        var company = await db.Companies.SingleAsync(c => c.Id == store.CompanyId, ct);
        return (company, store);
    }
}

/// <summary>Perfis tributários (CFOP, CSOSN/CST, PIS/COFINS) reutilizados pelos produtos.</summary>
public sealed class TaxProfileService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    public async Task<IReadOnlyList<TaxProfileModel>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var defaultId = await db.Companies.Select(c => c.DefaultTaxProfileId).FirstOrDefaultAsync(ct);
        var query = db.TaxProfiles.AsNoTracking();
        if (!includeInactive) query = query.Where(p => p.IsActive);
        var list = await query.OrderBy(p => p.Name).ToListAsync(ct);
        return list.Select(p => ToModel(p, p.Id == defaultId)).ToList();
    }

    public async Task<Result<Guid>> SaveAsync(Guid actorId, TaxProfileModel m, CancellationToken ct = default) =>
        await RunAsync(actorId, async () =>
        {
            var regime = await db.Companies.Select(c => c.TaxRegime).FirstAsync(ct);
            TaxProfile profile;
            if (m.Id is { } id)
            {
                profile = await db.TaxProfiles.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new DomainException("Perfil não encontrado.");
                profile.Update(m.Name, m.Cfop, m.Csosn, m.IcmsCst, m.IcmsRate, m.PisCst, m.PisRate, m.CofinsCst, m.CofinsRate);
            }
            else
            {
                profile = new TaxProfile(m.Name, m.Cfop, m.Csosn, m.IcmsCst, m.IcmsRate, m.PisCst, m.PisRate, m.CofinsCst, m.CofinsRate);
                db.TaxProfiles.Add(profile);
            }
            profile.EnsureCompatibleWith(regime);
            db.AuditEntries.Add(new AuditEntry(m.Id is null ? "taxprofile.created" : "taxprofile.updated", actorId, null, profile.Id,
                $"{profile.Name}: CFOP {profile.Cfop} {(profile.Csosn is null ? "CST " + profile.IcmsCst : "CSOSN " + profile.Csosn)}",
                clock.GetUtcNow()));
            return profile.Id;
        }, ct);

    public async Task<Result<Guid>> SetActiveAsync(Guid actorId, Guid profileId, bool active, CancellationToken ct = default) =>
        await RunAsync(actorId, async () =>
        {
            var profile = await db.TaxProfiles.SingleOrDefaultAsync(p => p.Id == profileId, ct) ?? throw new DomainException("Perfil não encontrado.");
            if (!active)
            {
                if (await db.Companies.AnyAsync(c => c.DefaultTaxProfileId == profileId, ct))
                    throw new DomainException("Este é o perfil padrão da empresa. Escolha outro padrão antes de desativá-lo.");
                profile.Deactivate();
            }
            else
            {
                profile.Activate();
            }
            return profile.Id;
        }, ct);

    internal static TaxProfileModel ToModel(TaxProfile p, bool isDefault) =>
        new(p.Id, p.Name, p.Cfop, p.Csosn, p.IcmsCst, p.IcmsRate, p.PisCst, p.PisRate, p.CofinsCst, p.CofinsRate, p.IsActive, isDefault);

    private async Task<Result<Guid>> RunAsync(Guid actorId, Func<Task<Guid>> operation, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageCompany, ct))
            return Result<Guid>.Fail("Usuário sem permissão para alterar a tributação.");
        try
        {
            var id = await operation();
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Ok(id);
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail(ex.Message);
        }
    }
}
