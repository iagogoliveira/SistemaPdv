using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;

namespace Pdv.Application.Licensing;

/// <summary>Conteúdo da licença (assinado pelo fornecedor do sistema).</summary>
/// <param name="MachineCode">Código do computador principal (servidor) — a licença só vale nele.</param>
/// <param name="ExpiresAt">Fim da validade (assinatura mensal/anual); nulo = licença perpétua.</param>
public sealed record LicenseData(
    string Id,
    string Customer,
    string? Cnpj,
    string Plan,
    int MaxTerminals,
    DateOnly IssuedAt,
    DateOnly? ExpiresAt,
    string MachineCode);

public enum LicenseState
{
    /// <summary>Sem licença, dentro do período de demonstração.</summary>
    Trial,
    Active,

    /// <summary>Vencida há poucos dias: tudo funciona, com aviso.</summary>
    Grace,

    /// <summary>Demonstração encerrada, licença vencida (após a tolerância) ou inválida: vendas bloqueadas.</summary>
    Restricted,
}

public sealed record LicenseStatus(LicenseState State, string Customer, string Plan, int MaxTerminals, int UsedTerminals,
    DateOnly? ExpiresAt, int? DaysLeft, string Message, string? MachineCode)
{
    public bool CanSell => State != LicenseState.Restricted;

    /// <summary>Aviso para o caixa (vencimento próximo, tolerância, modo restrito); nulo = nada a avisar.</summary>
    public string? Warning => State switch
    {
        LicenseState.Restricted => Message,
        LicenseState.Grace => Message,
        LicenseState.Trial when DaysLeft <= 7 => Message,
        LicenseState.Active when DaysLeft <= 15 => Message,
        _ => null,
    };
}

/// <summary>
/// Arquivo de licença: o JSON da licença em base64 e a assinatura ECDSA P-256 (SHA-256) do fornecedor.
/// Quem tem só a chave pública (o sistema) consegue conferir, mas não emitir.
/// </summary>
public static class LicenseCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Sign(LicenseData data, string privateKeyPem)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256);
        return JsonSerializer.Serialize(new LicenseFile(Convert.ToBase64String(payload), Convert.ToBase64String(signature)),
            Indented);
    }

    /// <summary>Confere a assinatura e devolve o conteúdo; lança <see cref="DomainException"/> se o arquivo for inválido.</summary>
    public static LicenseData Verify(string licenseText, string publicKeyPem)
    {
        LicenseFile? file;
        byte[] payload, signature;
        try
        {
            file = JsonSerializer.Deserialize<LicenseFile>(licenseText, Json);
            payload = Convert.FromBase64String(file?.Payload ?? "");
            signature = Convert.FromBase64String(file?.Signature ?? "");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new DomainException("Arquivo de licença inválido.");
        }
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (payload.Length == 0 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            throw new DomainException("Licença com assinatura inválida (arquivo alterado ou de outro fornecedor).");
        return JsonSerializer.Deserialize<LicenseData>(payload, Json) ?? throw new DomainException("Arquivo de licença inválido.");
    }

    public static (string PrivatePem, string PublicPem) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    private sealed record LicenseFile(string Payload, string Signature);
}

/// <summary>Regras de validade (puras, para teste).</summary>
public static class LicenseRules
{
    public const int TrialDays = 30;
    public const int TrialTerminals = 2;
    public const int GraceDays = 15;

    public static LicenseStatus Evaluate(LicenseData? license, string? invalidReason, DateOnly today, DateOnly trialStartedAt,
        string machineCode, int usedTerminals)
    {
        if (license is null)
        {
            var left = TrialDays - (today.DayNumber - trialStartedAt.DayNumber);
            return left > 0
                ? new LicenseStatus(LicenseState.Trial, "Demonstração", "Demonstração", TrialTerminals, usedTerminals, trialStartedAt.AddDays(TrialDays),
                    left, $"Versão de demonstração: {left} dia(s) restante(s). Instale a licença em Retaguarda › Sistema.", machineCode)
                : new LicenseStatus(LicenseState.Restricted, "Demonstração", "Demonstração", TrialTerminals, usedTerminals, trialStartedAt.AddDays(TrialDays),
                    0, invalidReason ?? "Período de demonstração encerrado: vendas bloqueadas até a instalação da licença (Retaguarda › Sistema).", machineCode);
        }

        if (!string.Equals(Normalize(license.MachineCode), Normalize(machineCode), StringComparison.Ordinal))
            return new LicenseStatus(LicenseState.Restricted, license.Customer, license.Plan, license.MaxTerminals, usedTerminals, license.ExpiresAt, null,
                "A licença instalada é de outro computador. Solicite a transferência ao fornecedor informando o código desta máquina.", machineCode);

        if (license.ExpiresAt is not { } expires)
            return new LicenseStatus(LicenseState.Active, license.Customer, license.Plan, license.MaxTerminals, usedTerminals, null, null,
                $"Licença {license.Plan} ativa para {license.Customer}.", machineCode);

        var daysLeft = expires.DayNumber - today.DayNumber;
        if (daysLeft >= 0)
            return new LicenseStatus(LicenseState.Active, license.Customer, license.Plan, license.MaxTerminals, usedTerminals, expires, daysLeft,
                daysLeft <= 15 ? $"A licença vence em {daysLeft} dia(s) ({expires:dd/MM/yyyy}). Renove com o fornecedor." : $"Licença {license.Plan} ativa até {expires:dd/MM/yyyy}.",
                machineCode);
        var graceLeft = GraceDays + daysLeft;
        return graceLeft > 0
            ? new LicenseStatus(LicenseState.Grace, license.Customer, license.Plan, license.MaxTerminals, usedTerminals, expires, graceLeft,
                $"Licença vencida em {expires:dd/MM/yyyy}. As vendas serão bloqueadas em {graceLeft} dia(s): renove com o fornecedor.", machineCode)
            : new LicenseStatus(LicenseState.Restricted, license.Customer, license.Plan, license.MaxTerminals, usedTerminals, expires, 0,
                $"Licença vencida em {expires:dd/MM/yyyy}: vendas bloqueadas até a renovação. Cadastros, relatórios e backup continuam disponíveis.", machineCode);
    }

    public static string Normalize(string code) => new string(code.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();

    public static string FormatMachineCode(byte[] hash)
    {
        var hex = Convert.ToHexString(hash)[..16];
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";
    }
}

/// <summary>Licença instalada e dados da máquina (implementado no servidor).</summary>
public interface ILicenseStore
{
    string? LoadText();
    void Save(string licenseText);
    string MachineCode { get; }
}

/// <summary>Bloqueios da licença nas operações (venda, abertura de caixa, novos caixas).</summary>
public interface ILicenseGuard
{
    Task EnsureCanSellAsync(CancellationToken ct = default);
    Task EnsureCanAddTerminalAsync(CancellationToken ct = default);
}

/// <summary>Sem servidor de licença (testes de serviços): nada é bloqueado.</summary>
public sealed class UnrestrictedLicenseGuard : ILicenseGuard
{
    public Task EnsureCanSellAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureCanAddTerminalAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Licenciamento do sistema: confere a licença instalada no servidor (assinatura, máquina, validade), conta os caixas
/// conectados e aplica os limites. Sem licença, vale a demonstração a partir da primeira inicialização do banco.
/// </summary>
/// <summary>Chave pública que confere as licenças (a privada fica só com o fornecedor).</summary>
public sealed record LicensePublicKey(string Pem);

public sealed class LicenseService(IPdvDbContext db, ILicenseStore store, PermissionChecker permissions, TimeProvider clock, LicensePublicKey key)
    : ILicenseGuard
{
    private readonly string publicKeyPem = key.Pem;

    public const string TrialStartKey = "trial.started_at";

    /// <summary>Situação para o caixa e a retaguarda (o código da máquina só para quem administra o sistema).</summary>
    public async Task<LicenseStatus> GetStatusAsync(Guid? actorId = null, CancellationToken ct = default)
    {
        var status = await EvaluateAsync(ct);
        var canSeeCode = actorId is { } id && await permissions.HasAsync(id, Permissions.ManageSystem, ct);
        return canSeeCode ? status : status with { MachineCode = null };
    }

    public async Task<Result<LicenseStatus>> InstallAsync(Guid actorId, string licenseText, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageSystem, ct))
            return Result<LicenseStatus>.Fail("Seu usuário não tem permissão para administrar o sistema.");
        LicenseData data;
        try { data = LicenseCodec.Verify(licenseText, publicKeyPem); }
        catch (DomainException ex) { return Result<LicenseStatus>.Fail(ex.Message); }
        if (LicenseRules.Normalize(data.MachineCode) != LicenseRules.Normalize(store.MachineCode))
            return Result<LicenseStatus>.Fail($"Esta licença é do computador {data.MachineCode}; este servidor é {store.MachineCode}.");
        if (data.Cnpj is { Length: > 0 } cnpj && await db.Companies.Select(c => c.Cnpj).FirstOrDefaultAsync(ct) is { Length: > 0 } companyCnpj
            && companyCnpj != cnpj)
            return Result<LicenseStatus>.Fail("A licença é de outra empresa (CNPJ diferente do cadastrado em Retaguarda › Empresa).");

        store.Save(licenseText);
        db.AuditEntries.Add(new AuditEntry("license.installed", actorId, null, null,
            $"{data.Id} {data.Plan} {data.MaxTerminals} caixa(s) até {data.ExpiresAt?.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "sem vencimento"}", clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return Result<LicenseStatus>.Ok(await GetStatusAsync(actorId, ct));
    }

    // Implementações explícitas: não são expostas aos caixas como chamadas remotas.
    async Task ILicenseGuard.EnsureCanSellAsync(CancellationToken ct)
    {
        var status = await EvaluateAsync(ct);
        if (!status.CanSell) throw new DomainException(status.Message);
    }

    async Task ILicenseGuard.EnsureCanAddTerminalAsync(CancellationToken ct)
    {
        var status = await EvaluateAsync(ct);
        if (!status.CanSell) throw new DomainException(status.Message);
        if (status.UsedTerminals >= status.MaxTerminals)
            throw new DomainException($"Limite da licença atingido: {status.MaxTerminals} caixa(s) ({status.Plan}). " +
                "Desconecte um caixa em Retaguarda › Sistema ou amplie o plano com o fornecedor.");
    }

    private async Task<LicenseStatus> EvaluateAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var used = await db.Terminals.CountAsync(t => t.IsActive && t.AccessKeyHash != null, ct);
        LicenseData? data = null;
        string? invalid = null;
        if (store.LoadText() is { Length: > 0 } text)
        {
            try { data = LicenseCodec.Verify(text, publicKeyPem); }
            catch (DomainException ex) { invalid = ex.Message; }
        }
        var trialStart = await TrialStartAsync(today, ct);
        return LicenseRules.Evaluate(data, invalid, today, trialStart, store.MachineCode, used);
    }

    private async Task<DateOnly> TrialStartAsync(DateOnly today, CancellationToken ct)
    {
        var stored = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == TrialStartKey, ct);
        if (stored is not null && DateOnly.TryParse(stored.Value, System.Globalization.CultureInfo.InvariantCulture, out var date)) return date;
        db.AppSettings.Add(new Domain.Common.AppSetting(TrialStartKey, today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ResetChangeTracker(); }
        return today;
    }
}
