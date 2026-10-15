using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Pdv.Application;
using Pdv.Application.Fiscal;
using Pdv.Application.Identity;
using Pdv.Application.Licensing;
using Pdv.Application.Maintenance;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;

namespace Pdv.Server;

/// <summary>Checklist de implantação montado a partir do estado real do servidor (banco, NFC-e, licença, backup).</summary>
public sealed class SetupAssistant(
    IPdvDbContext db,
    PermissionChecker permissions,
    INfceSettingsStore nfce,
    INfceGateway gateway,
    TaxApproximationService taxes,
    LicenseService license,
    BackupRunner backups,
    TimeProvider clock) : ISetupAssistant
{
    public const string TaxReviewedKey = "setup.tax_reviewed";
    public const string SefazTestKey = "setup.sefaz_test";
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public async Task<SetupChecklist> GetAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        var items = new List<SetupItem>();
        var company = await db.Companies.AsNoTracking().OrderBy(c => c.Id).FirstAsync(ct);
        var settings = nfce.Load();
        var now = clock.GetLocalNow();

        // 1) Empresa
        var missing = new List<string>();
        if (company.LegalName == "Minha Empresa") missing.Add("razão social");
        if (company.Cnpj is null) missing.Add("CNPJ");
        if (company.StateRegistration is null) missing.Add("inscrição estadual");
        if (company.Address is null) missing.Add("endereço com código IBGE");
        items.Add(missing.Count == 0
            ? new SetupItem("empresa", "Dados da empresa", SetupState.Ok,
                $"{company.LegalName} · CNPJ {TaxDocuments.Format(company.Cnpj)} · IE {company.StateRegistration} · {company.Address!.City}/{company.Address.Uf}", "Empresa")
            : new SetupItem("empresa", "Dados da empresa", SetupState.Pending, $"Falta: {string.Join(", ", missing)}.", "Empresa"));

        // 2) Tributação
        var reviewed = await db.AppSettings.AsNoTracking().AnyAsync(s => s.Key == TaxReviewedKey, ct);
        items.Add(company.DefaultTaxProfileId is null
            ? new SetupItem("tributacao", "Tributação", SetupState.Pending, "Escolha o perfil tributário padrão da empresa.", "Tributação")
            : reviewed
                ? new SetupItem("tributacao", "Tributação", SetupState.Ok, "Perfis tributários conferidos pelo contador.", "Tributação")
                : new SetupItem("tributacao", "Tributação", SetupState.Warning,
                    "Peça ao contador para conferir CFOP, CSOSN/CST e PIS/COFINS dos perfis; depois clique em \"Contador conferiu\".", "Tributação"));

        // 3) Produtos
        var active = await db.Products.CountAsync(p => p.IsActive, ct);
        var withoutNcm = await db.Products.CountAsync(p => p.IsActive && p.Ncm == null, ct);
        items.Add(active == 0
            ? new SetupItem("produtos", "Produtos", SetupState.Pending, "Nenhum produto cadastrado. Cadastre ou importe pelos XMLs de compra.", "Produtos")
            : withoutNcm > 0
                ? new SetupItem("produtos", "Produtos", SetupState.Pending, $"{withoutNcm} de {active} produto(s) sem NCM: a NFC-e não sai para eles.", "Produtos")
                : new SetupItem("produtos", "Produtos", SetupState.Ok, $"{active} produto(s) ativos, todos com NCM.", "Produtos"));

        // 4) Tabela IBPT (Lei 12.741)
        var table = await taxes.GetStatusAsync(ct);
        items.Add(table.Rows == 0
            ? new SetupItem("ibpt", "Tributos aproximados (IBPT)", SetupState.Pending,
                "Importe a tabela IBPT da sua UF (baixe com o CNPJ em deolhonoimposto.ibpt.org.br) — exigida pela Lei 12.741/2012.", "Tributação")
            : table.Expired
                ? new SetupItem("ibpt", "Tributos aproximados (IBPT)", SetupState.Warning,
                    $"Tabela {table.Version} vencida em {table.ValidTo:dd/MM/yyyy}: importe a versão atual.", "Tributação")
                : table.ProductsWithoutRate > 0
                    ? new SetupItem("ibpt", "Tributos aproximados (IBPT)", SetupState.Warning,
                        $"Tabela {table.Version} carregada, mas {table.ProductsWithoutRate} produto(s) têm NCM fora dela (confira o NCM).", "Tributação")
                    : new SetupItem("ibpt", "Tributos aproximados (IBPT)", SetupState.Ok,
                        $"Tabela {table.Version} ({table.Source}) válida até {table.ValidTo:dd/MM/yyyy}.", "Tributação"));

        // 5) Certificado A1
        items.Add(CertificateItem(settings, company.Cnpj, now));

        // 6) CSC
        items.Add(!string.IsNullOrWhiteSpace(settings.CscId) && !string.IsNullOrWhiteSpace(settings.CscToken)
            ? new SetupItem("csc", "CSC (código de segurança)", SetupState.Ok, $"ID {settings.CscId} informado.", "NFC-e")
            : new SetupItem("csc", "CSC (código de segurança)", SetupState.Pending,
                "Gere o CSC no portal da SEFAZ do estado (um de homologação, outro de produção) e informe ID e código.", "NFC-e"));

        // 7) Emissão
        items.Add(!settings.Enabled
            ? new SetupItem("nfce", "Emissão de NFC-e", SetupState.Pending, "Desligada: marque \"Emitir NFC-e nas vendas\" quando os itens acima estiverem prontos.", "NFC-e")
            : settings.Environment == FiscalEnvironment.Homologation
                ? new SetupItem("nfce", "Emissão de NFC-e", SetupState.Warning,
                    $"Ligada em HOMOLOGAÇÃO (série {settings.Series}): notas sem valor fiscal. Depois das notas de teste, mude para produção com o CSC de produção.", "NFC-e")
                : new SetupItem("nfce", "Emissão de NFC-e", SetupState.Ok, $"Ligada em produção, série {settings.Series}.", "NFC-e"));

        // 8) Teste da SEFAZ
        var test = await db.AppSettings.AsNoTracking().Where(s => s.Key == SefazTestKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        items.Add(SefazItem(test));

        // 9) Usuários
        var users = await db.Users.CountAsync(u => u.IsActive, ct);
        items.Add(users > 1
            ? new SetupItem("usuarios", "Usuários", SetupState.Ok, $"{users} usuário(s) ativos.", "Usuários")
            : new SetupItem("usuarios", "Usuários", SetupState.Warning, "Só o administrador: cadastre os operadores do caixa (cada um com seu login).", "Usuários"));

        // 10) Caixas
        var terminals = await db.Terminals.CountAsync(t => t.IsActive && t.AccessKeyHash != null, ct);
        items.Add(terminals > 0
            ? new SetupItem("caixas", "Caixas conectados", SetupState.Ok, $"{terminals} caixa(s) conectado(s) ao servidor.", "Sistema")
            : new SetupItem("caixas", "Caixas conectados", SetupState.Pending, "Nenhum caixa conectado.", "Sistema"));

        // 11) Licença
        var lic = await license.GetStatusAsync(null, ct);
        items.Add(new SetupItem("licenca", "Licença", lic.State switch
        {
            LicenseState.Active => SetupState.Ok,
            LicenseState.Restricted => SetupState.Pending,
            _ => SetupState.Warning,
        }, lic.Message, "Sistema"));

        // 12) Backup
        var (last, error) = backups.LastRun();
        var copy = backups.LoadSettings().CopyFolder;
        items.Add(error is not null
            ? new SetupItem("backup", "Backup", SetupState.Warning, error, "Sistema")
            : last is null
                ? new SetupItem("backup", "Backup", SetupState.Warning, "Nenhum backup feito ainda: use \"Fazer backup agora\".", "Sistema")
                : copy is null
                    ? new SetupItem("backup", "Backup", SetupState.Warning,
                        $"Último backup {last.Value.ToLocalTime():dd/MM HH:mm}, só neste computador: configure a segunda cópia (pendrive, rede ou nuvem).", "Sistema")
                    : new SetupItem("backup", "Backup", SetupState.Ok, $"Último backup {last.Value.ToLocalTime():dd/MM HH:mm}, com cópia em {copy}.", "Sistema"));

        return new SetupChecklist(items);
    }

    public async Task<SetupChecklist> ConfirmTaxReviewAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        var value = $"{clock.GetUtcNow():O}|{actorId}";
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == TaxReviewedKey, ct);
        if (row is null) db.AppSettings.Add(new AppSetting(TaxReviewedKey, value));
        else row.Set(value);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actorId, ct);
    }

    public async Task<Result<string>> TestSefazAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        var settings = nfce.Load();
        if (!settings.IsComplete || string.IsNullOrEmpty(settings.CertificatePassword))
            return Result<string>.Fail("Informe certificado, senha e CSC na aba NFC-e antes de testar.");
        var uf = await db.Companies.Select(c => c.Address!.Uf).FirstOrDefaultAsync(ct);
        if (uf is null) return Result<string>.Fail("Informe o endereço (UF) da empresa antes de testar.");

        SefazResult result;
        try { result = await gateway.ServiceStatusAsync(settings, uf, ct); }
        catch (DomainException ex) { return Result<string>.Fail(ex.Message); }

        var ok = result.Outcome == SefazOutcome.Authorized;
        var env = settings.Environment == FiscalEnvironment.Homologation ? "homologação" : "produção";
        var value = $"{(ok ? "ok" : "erro")}|{clock.GetUtcNow():O}|{env}|{result.StatusCode} {result.Message}";
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == SefazTestKey, ct);
        if (row is null) db.AppSettings.Add(new AppSetting(SefazTestKey, value.Length > 500 ? value[..500] : value));
        else row.Set(value.Length > 500 ? value[..500] : value);
        await db.SaveChangesAsync(ct);
        return ok
            ? Result<string>.Ok($"SEFAZ-{uf} ({env}) respondeu: {result.StatusCode} - {result.Message}")
            : Result<string>.Fail($"A SEFAZ-{uf} ({env}) não respondeu como esperado: {result.StatusCode} - {result.Message}");
    }

    private static SetupItem CertificateItem(NfceSettings settings, string? companyCnpj, DateTimeOffset now)
    {
        const string title = "Certificado digital A1";
        if (string.IsNullOrWhiteSpace(settings.CertificatePath) || !File.Exists(settings.CertificatePath))
            return new SetupItem("certificado", title, SetupState.Pending, "Envie o arquivo .pfx do e-CNPJ A1 e a senha.", "NFC-e");
        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword);
            if (cert.NotAfter < now.DateTime)
                return new SetupItem("certificado", title, SetupState.Pending, $"Vencido em {cert.NotAfter:dd/MM/yyyy}: renove com a certificadora.", "NFC-e");
            if (companyCnpj is not null && !cert.Subject.Contains(companyCnpj, StringComparison.Ordinal))
                return new SetupItem("certificado", title, SetupState.Warning,
                    $"O certificado não parece ser do CNPJ {TaxDocuments.Format(companyCnpj)} ({cert.GetNameInfo(X509NameType.SimpleName, false)}).", "NFC-e");
            var days = (cert.NotAfter - now.DateTime).TotalDays;
            return new SetupItem("certificado", title, days < 30 ? SetupState.Warning : SetupState.Ok,
                $"{cert.GetNameInfo(X509NameType.SimpleName, false)} · válido até {cert.NotAfter.ToString("dd/MM/yyyy", PtBr)}" +
                (days < 30 ? " — renove já." : "."), "NFC-e");
        }
        catch (CryptographicException)
        {
            return new SetupItem("certificado", title, SetupState.Pending, "Não foi possível abrir o certificado: confira a senha.", "NFC-e");
        }
    }

    private static SetupItem SefazItem(string? stored)
    {
        const string title = "Comunicação com a SEFAZ";
        var parts = stored?.Split('|', 4);
        if (parts is not { Length: 4 })
            return new SetupItem("sefaz", title, SetupState.Warning, "Ainda não testada: clique em \"Testar SEFAZ\".", "NFC-e");
        var when = DateTimeOffset.Parse(parts[1], CultureInfo.InvariantCulture).ToLocalTime();
        return parts[0] == "ok"
            ? new SetupItem("sefaz", title, SetupState.Ok, $"Respondendo ({parts[2]}) em {when:dd/MM HH:mm}: {parts[3]}", "NFC-e")
            : new SetupItem("sefaz", title, SetupState.Warning, $"Falhou ({parts[2]}) em {when:dd/MM HH:mm}: {parts[3]}", "NFC-e");
    }

    private async Task RequireAsync(Guid actorId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageCompany, ct) && !await permissions.HasAsync(actorId, Permissions.ManageSystem, ct))
            throw new DomainException("Seu usuário não tem permissão para a implantação.");
    }
}
