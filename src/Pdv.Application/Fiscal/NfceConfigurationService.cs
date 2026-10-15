using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;

namespace Pdv.Application.Fiscal;

/// <summary>Configuração da NFC-e como a retaguarda vê: os segredos nunca saem do servidor.</summary>
public sealed record NfceConfigView(bool Enabled, FiscalEnvironment Environment, int Series, bool HasCertificate,
    bool HasCertificatePassword, string? CscId, bool HasCscToken);

/// <summary>Alteração da configuração. Campos de segredo nulos ou vazios mantêm o valor já salvo.</summary>
public sealed record NfceConfigUpdate(bool Enabled, FiscalEnvironment Environment, int Series, byte[]? CertificatePfx,
    string? CertificatePassword, string? CscId, string? CscToken);

/// <summary>
/// Configuração de emissão guardada no servidor da loja (certificado A1 e CSC cifrados com DPAPI).
/// Todos os caixas emitem com o mesmo certificado e a mesma série; a numeração é reservada no banco.
/// </summary>
public sealed class NfceConfigurationService(INfceSettingsStore store, PermissionChecker permissions, IPdvDbContext db, TimeProvider clock)
{
    /// <summary>Usado pelo caixa para saber se emite NFC-e após a venda (sem exigir permissão).</summary>
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(store.Load().Enabled);

    public async Task<NfceConfigView> GetAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        return ToView(store.Load());
    }

    public async Task<Result<NfceConfigView>> SaveAsync(Guid actorId, NfceConfigUpdate update, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        if (update.Series is < 0 or > 999) return Result<NfceConfigView>.Fail("Série inválida (0 a 999).");

        var current = store.Load();
        var password = string.IsNullOrEmpty(update.CertificatePassword) ? current.CertificatePassword : update.CertificatePassword;
        if (update.CertificatePfx is { Length: > 0 } pfx)
        {
            // Confere o arquivo antes de substituir o certificado em uso.
            if (string.IsNullOrEmpty(password)) return Result<NfceConfigView>.Fail("Informe a senha do certificado.");
            try
            {
                using var cert = X509CertificateLoader.LoadPkcs12(pfx, password);
                if (cert.NotAfter < clock.GetLocalNow().DateTime)
                    return Result<NfceConfigView>.Fail($"O certificado venceu em {cert.NotAfter:dd/MM/yyyy}.");
            }
            catch (CryptographicException)
            {
                return Result<NfceConfigView>.Fail("Não foi possível abrir o certificado: arquivo inválido ou senha incorreta.");
            }
        }

        var settings = current with
        {
            Enabled = update.Enabled,
            Environment = update.Environment,
            Series = update.Series,
            CertificatePath = update.CertificatePfx is { Length: > 0 } ? store.ImportCertificate(update.CertificatePfx) : current.CertificatePath,
            CertificatePassword = password,
            CscId = string.IsNullOrWhiteSpace(update.CscId) ? null : update.CscId.Trim(),
            CscToken = string.IsNullOrEmpty(update.CscToken) ? current.CscToken : update.CscToken.Trim(),
        };
        if (settings.Enabled && (!settings.IsComplete || string.IsNullOrEmpty(settings.CertificatePassword)))
            return Result<NfceConfigView>.Fail("Para ativar a NFC-e informe o certificado (.pfx), a senha, o ID e o código CSC.");

        store.Save(settings);
        db.AuditEntries.Add(new AuditEntry("nfce.settings_updated", actorId, null, null,
            $"{(settings.Enabled ? "ativada" : "desativada")}, {settings.Environment}, série {settings.Series}", clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return Result<NfceConfigView>.Ok(ToView(settings));
    }

    private static NfceConfigView ToView(NfceSettings s) => new(s.Enabled, s.Environment, s.Series,
        !string.IsNullOrEmpty(s.CertificatePath), !string.IsNullOrEmpty(s.CertificatePassword), s.CscId, !string.IsNullOrEmpty(s.CscToken));

    private async Task RequireAsync(Guid actorId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageCompany, ct))
            throw new DomainException("Seu usuário não tem permissão para configurar a NFC-e.");
    }
}
