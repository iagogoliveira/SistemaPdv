using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;
using Pdv.Domain.Payments;
using Pdv.Domain.Sales;

namespace Pdv.Application.Payments;

/// <summary>
/// Pix com QR Code dinâmico: o caixa pede a cobrança, mostra o QR e consulta a situação até o PSP confirmar.
/// A venda só aceita um Pix integrado confirmado (ver <see cref="Sales.CheckoutService"/>).
/// </summary>
public sealed class PixService(
    IPdvDbContext db,
    IPixSettingsStore settingsStore,
    IEnumerable<IPixProvider> providers,
    PermissionChecker permissions,
    TimeProvider clock)
{
    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var s = settingsStore.Load();
        return Task.FromResult(s.Enabled && s.IsComplete);
    }

    /// <summary>Gera (ou reaproveita, para o mesmo carrinho e valor) a cobrança deste caixa.</summary>
    public async Task<PixChargeView> CreateChargeAsync(Guid terminalId, Guid clientRequestId, decimal amount, CancellationToken ct = default)
    {
        var settings = RequireSettings();
        amount = Money.Round(amount);
        if (amount <= 0) throw new DomainException("Valor do Pix deve ser positivo.");
        var now = clock.GetUtcNow();

        var reusable = await db.PixCharges.Where(c => c.TerminalId == terminalId && c.ClientRequestId == clientRequestId
                && c.Amount == amount && c.Status == PixChargeStatus.Active)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (reusable is not null && reusable.ExpiresAt > now.AddSeconds(30)) return PixChargeView.From(reusable);

        var provider = Provider(settings);
        var txid = PixCharge.NewTxid();
        PspChargeCreated created;
        try
        {
            created = await provider.CreateAsync(new PspChargeRequest(txid, amount, settings.ExpirationSeconds, "Compra na loja"), settings, ct);
        }
        catch (PixProviderException ex)
        {
            throw new DomainException($"Não foi possível gerar o Pix: {ex.Message}");
        }
        var charge = new PixCharge(terminalId, clientRequestId, amount, provider.Name, txid, created.CopyPaste, now, created.ExpiresAt);
        db.PixCharges.Add(charge);
        await db.SaveChangesAsync(ct);
        return PixChargeView.From(charge);
    }

    /// <summary>Consulta o PSP (se a cobrança ainda está aberta) e devolve a situação.</summary>
    public async Task<PixChargeView> CheckAsync(Guid terminalId, Guid chargeId, CancellationToken ct = default)
    {
        var charge = await db.PixCharges.SingleOrDefaultAsync(c => c.Id == chargeId && c.TerminalId == terminalId, ct)
            ?? throw new DomainException("Cobrança Pix não encontrada.");
        await RefreshAsync(charge, ct);
        return PixChargeView.From(charge);
    }

    /// <summary>
    /// Desiste da cobrança. Se o cliente já pagou, a cobrança volta como paga (o caixa deve usá-la ou devolvê-la).
    /// </summary>
    public async Task<PixChargeView> CancelAsync(Guid terminalId, Guid chargeId, CancellationToken ct = default)
    {
        var charge = await db.PixCharges.SingleOrDefaultAsync(c => c.Id == chargeId && c.TerminalId == terminalId, ct)
            ?? throw new DomainException("Cobrança Pix não encontrada.");
        await RefreshAsync(charge, ct);
        if (charge.Status != PixChargeStatus.Active) return PixChargeView.From(charge);

        var settings = settingsStore.Load();
        try
        {
            await Provider(settings).CancelAsync(charge.Txid, settings, ct);
            charge.Cancel("Cancelada no caixa.");
        }
        catch (PixProviderException ex)
        {
            // Sem confirmação do PSP a cobrança fica aberta até expirar; o servidor continua acompanhando.
            charge.SetMessage($"Cancelamento não confirmado pelo PSP: {ex.Message}");
        }
        await db.SaveChangesAsync(ct);
        return PixChargeView.From(charge);
    }

    /// <summary>Atualiza cobranças abertas ou recém-vencidas (chamado periodicamente pelo servidor).</summary>
    public async Task<int> RefreshOpenAsync(CancellationToken ct = default)
    {
        var settings = settingsStore.Load();
        if (!settings.Enabled || !settings.IsComplete) return 0;
        var since = clock.GetUtcNow().AddHours(-1);
        var open = await db.PixCharges.Where(c => c.Status == PixChargeStatus.Active && c.CreatedAt > since)
            .OrderBy(c => c.CreatedAt).Take(50).ToListAsync(ct);
        var paid = 0;
        foreach (var charge in open)
        {
            await RefreshAsync(charge, ct);
            if (charge.Status == PixChargeStatus.Paid) paid++;
        }
        return paid;
    }

    public async Task<IReadOnlyList<PixChargeRow>> ListAsync(Guid actorId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ViewReports, ct))
            throw new DomainException("Seu usuário não tem permissão para ver os pagamentos.");
        var period = new Reports.ReportPeriod(from, to);
        var start = period.StartUtc(TimeZoneInfo.Local);
        var end = period.EndUtc(TimeZoneInfo.Local);
        return await (
            from c in db.PixCharges.AsNoTracking()
            join t in db.Terminals on c.TerminalId equals t.Id
            join s in db.Sales on c.SaleId equals s.Id into sales
            from s in sales.DefaultIfEmpty()
            where c.CreatedAt >= start && c.CreatedAt < end
            orderby c.CreatedAt descending
            select new PixChargeRow(c.Id, c.CreatedAt, t.Code, c.Amount, c.Status, s == null ? null : s.Number,
                s != null && s.Status == SaleStatus.Cancelled, c.EndToEndId, c.PayerName, c.Message))
            .ToListAsync(ct);
    }

    /// <summary>Consulta no PSP pela retaguarda (qualquer caixa), para conferir um Pix ainda em aberto.</summary>
    public async Task<PixChargeView> RefreshAsync(Guid actorId, Guid chargeId, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ViewReports, ct))
            throw new DomainException("Seu usuário não tem permissão para ver os pagamentos.");
        var charge = await db.PixCharges.SingleOrDefaultAsync(c => c.Id == chargeId, ct)
            ?? throw new DomainException("Cobrança Pix não encontrada.");
        await RefreshAsync(charge, ct);
        return PixChargeView.From(charge);
    }

    /// <summary>Devolve ao pagador um Pix pago sem venda concluída, ou de venda cancelada.</summary>
    public async Task<Result<PixChargeView>> RefundAsync(Guid actorId, Guid chargeId, string reason, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.CancelSale, ct))
            return Result<PixChargeView>.Fail("Seu usuário não tem permissão para devolver pagamentos.");
        if (string.IsNullOrWhiteSpace(reason)) return Result<PixChargeView>.Fail("Informe o motivo da devolução.");
        var charge = await db.PixCharges.SingleOrDefaultAsync(c => c.Id == chargeId, ct);
        if (charge is null) return Result<PixChargeView>.Fail("Cobrança Pix não encontrada.");
        if (charge.Status != PixChargeStatus.Paid || charge.EndToEndId is null) return Result<PixChargeView>.Fail("Só um Pix pago pode ser devolvido.");
        if (charge.SaleId is { } saleId && await db.Sales.AnyAsync(s => s.Id == saleId && s.Status != SaleStatus.Cancelled, ct))
            return Result<PixChargeView>.Fail("Este Pix quitou uma venda ativa. Cancele a venda (F7) antes de devolver.");

        var settings = settingsStore.Load();
        PspRefundStatus status;
        try
        {
            status = await Provider(settings).RefundAsync(charge.EndToEndId, "D" + charge.Txid[..30], charge.Amount, settings, ct);
        }
        catch (PixProviderException ex)
        {
            return Result<PixChargeView>.Fail($"O PSP não aceitou a devolução: {ex.Message}");
        }
        if (status == PspRefundStatus.Failed) return Result<PixChargeView>.Fail("O PSP não realizou a devolução.");

        charge.MarkRefunded(status == PspRefundStatus.Refunded ? $"Devolvido: {reason.Trim()}" : $"Devolução em processamento: {reason.Trim()}");
        db.AuditEntries.Add(new AuditEntry("pix.refunded", actorId, charge.TerminalId, charge.Id, $"{charge.Amount:N2} {reason.Trim()}", clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return Result<PixChargeView>.Ok(PixChargeView.From(charge));
    }

    private async Task RefreshAsync(PixCharge charge, CancellationToken ct)
    {
        if (charge.Status != PixChargeStatus.Active) return;
        var settings = settingsStore.Load();
        var now = clock.GetUtcNow();
        try
        {
            var state = await Provider(settings).GetAsync(charge.Txid, settings, ct);
            switch (state.Status)
            {
                case PspChargeStatus.Paid:
                    charge.MarkPaid(state.EndToEndId ?? throw new PixProviderException("PSP não informou o EndToEndId."),
                        state.PaidAmount ?? charge.Amount, state.PayerName, state.PaidAt ?? now);
                    break;
                case PspChargeStatus.Removed:
                    charge.Cancel("Removida no PSP.");
                    break;
                default:
                    charge.Expire(now);
                    break;
            }
        }
        catch (PixProviderException ex)
        {
            charge.SetMessage($"Sem resposta do PSP: {ex.Message}");
        }
        catch (DomainException ex)
        {
            charge.SetMessage(ex.Message); // ex.: valor pago diferente — fica para conferência na retaguarda
        }
        await db.SaveChangesAsync(ct);
    }

    private PixSettings RequireSettings()
    {
        var settings = settingsStore.Load();
        if (!settings.Enabled || !settings.IsComplete)
            throw new DomainException("Pix integrado não configurado. Configure em Retaguarda › Pagamentos.");
        return settings;
    }

    private IPixProvider Provider(PixSettings settings) =>
        providers.FirstOrDefault(p => p.Name == settings.Provider)
        ?? throw new DomainException($"Provedor Pix desconhecido: {settings.Provider}.");
}

/// <summary>Retaguarda › Pagamentos: configuração do Pix (segredos ficam no servidor).</summary>
public sealed class PixConfigurationService(IPixSettingsStore store, IEnumerable<IPixProvider> providers, PermissionChecker permissions,
    IPdvDbContext db, TimeProvider clock)
{
    public async Task<PixConfigView> GetAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        return ToView(store.Load());
    }

    public async Task<Result<PixConfigView>> SaveAsync(Guid actorId, PixConfigUpdate u, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        if (providers.All(p => p.Name != u.Provider)) return Result<PixConfigView>.Fail("Provedor Pix inválido.");
        if (u.ExpirationSeconds is < 60 or > 3600) return Result<PixConfigView>.Fail("Validade do QR Code: de 60 a 3600 segundos.");
        if (u.PspCnpj is { Length: > 0 } cnpj && !Domain.Fiscal.TaxDocuments.IsValidCnpj(new string(cnpj.Where(char.IsAsciiLetterOrDigit).ToArray())))
            return Result<PixConfigView>.Fail("CNPJ do PSP inválido.");
        if (u.BaseUrl is { Length: > 0 } url && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            return Result<PixConfigView>.Fail("O endereço da API Pix deve começar com https://.");

        var current = store.Load();
        var certPassword = string.IsNullOrEmpty(u.CertificatePassword) ? current.CertificatePassword : u.CertificatePassword;
        if (u.CertificatePfx is { Length: > 0 } pfx)
        {
            try { using var _ = X509CertificateLoader.LoadPkcs12(pfx, certPassword); }
            catch (CryptographicException) { return Result<PixConfigView>.Fail("Não foi possível abrir o certificado do PSP: arquivo inválido ou senha incorreta."); }
        }

        var settings = current with
        {
            Enabled = u.Enabled,
            Provider = u.Provider,
            BaseUrl = Clean(u.BaseUrl)?.TrimEnd('/'),
            TokenUrl = Clean(u.TokenUrl),
            ClientId = Clean(u.ClientId),
            ClientSecret = string.IsNullOrEmpty(u.ClientSecret) ? current.ClientSecret : u.ClientSecret,
            CertificatePath = u.CertificatePfx is { Length: > 0 } ? store.ImportCertificate(u.CertificatePfx) : current.CertificatePath,
            CertificatePassword = certPassword,
            PixKey = Clean(u.PixKey),
            PspCnpj = u.PspCnpj is { Length: > 0 } ? new string(u.PspCnpj.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant() : null,
            PspName = Clean(u.PspName),
            ExpirationSeconds = u.ExpirationSeconds,
        };
        if (settings.Enabled && !settings.IsComplete)
            return Result<PixConfigView>.Fail("Para ativar o Pix integrado informe o endereço da API, o client ID, o client secret e a chave Pix.");

        store.Save(settings);
        db.AuditEntries.Add(new AuditEntry("pix.settings_updated", actorId, null, null,
            $"{(settings.Enabled ? "ativado" : "desativado")}, {settings.Provider}", clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return Result<PixConfigView>.Ok(ToView(settings));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PixConfigView ToView(PixSettings s) => new(s.Enabled, s.Provider, s.BaseUrl, s.TokenUrl, s.ClientId,
        !string.IsNullOrEmpty(s.ClientSecret), !string.IsNullOrEmpty(s.CertificatePath), s.PixKey, s.PspCnpj, s.PspName, s.ExpirationSeconds);

    private async Task RequireAsync(Guid actorId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageCompany, ct))
            throw new DomainException("Seu usuário não tem permissão para configurar pagamentos.");
    }
}
