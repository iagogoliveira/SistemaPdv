using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Sales;

namespace Pdv.Application.Fiscal;

public sealed record NfceInfo(Guid DocumentId, Guid SaleId, long SaleNumber, int Series, long Number, FiscalEnvironment Environment,
    FiscalEmissionType EmissionType, FiscalDocumentStatus Status, string AccessKey, string? Protocol, int? StatusCode,
    string? StatusMessage, DateTimeOffset? IssuedAt, DateTimeOffset? AuthorizedAt, string? QrCodeUrl, string? ConsultationUrl,
    decimal Total, int Attempts);

/// <summary>
/// Emissão da NFC-e de uma venda: reserva o número, monta e assina o XML, envia à SEFAZ e trata o resultado.
/// Regras de segurança:
/// - Reenvio seguro: nota sem resposta ou em contingência é primeiro CONSULTADA pela chave; só reenvia se a SEFAZ não a conhece.
/// - Duplicidade (cStat 204) também vira consulta, nunca um segundo número.
/// - SEFAZ indisponível: a nota é emitida em contingência offline (tpEmis 9) e transmitida depois.
/// </summary>
public sealed class NfceService(
    IPdvDbContext db,
    INfceGateway gateway,
    INfceSettingsStore settingsStore,
    PermissionChecker permissions,
    TimeProvider clock)
{
    private const int DuplicateStatusCode = 204;

    public async Task<Result<NfceInfo>> IssueForSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        try
        {
            var settings = RequireSettings();
            var context = await LoadContextAsync(saleId, ct);

            var document = await db.FiscalDocuments.SingleOrDefaultAsync(d => d.SaleId == saleId, ct)
                ?? await ReserveDocumentAsync(context, settings, ct);
            if (!document.IsFinal) await TransmitAsync(document, context, settings, ct);
            return Result<NfceInfo>.Ok(ToInfo(document, context.Sale));
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<NfceInfo>.Fail(ex.Message);
        }
    }

    /// <summary>Transmite notas pendentes, sem resposta ou em contingência (mais antigas primeiro).</summary>
    public async Task<int> ProcessPendingAsync(int take = 20, CancellationToken ct = default)
    {
        var settings = settingsStore.Load();
        if (!settings.Enabled || !settings.IsComplete) return 0;

        var saleIds = await db.FiscalDocuments
            .Where(d => d.Status == FiscalDocumentStatus.Pending || d.Status == FiscalDocumentStatus.Unknown
                || d.Status == FiscalDocumentStatus.Contingency)
            .OrderBy(d => d.CreatedAt).Take(take).Select(d => d.SaleId).ToListAsync(ct);

        var resolved = 0;
        foreach (var saleId in saleIds)
        {
            db.ResetChangeTracker();
            var result = await IssueForSaleAsync(saleId, ct);
            if (result.Succeeded && result.Value!.Status == FiscalDocumentStatus.Authorized) resolved++;
        }
        return resolved;
    }

    public async Task<Result<NfceInfo>> CancelAsync(Guid saleId, string justification, Guid userId, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(userId, Permissions.CancelSale, ct))
            return Result<NfceInfo>.Fail("Usuário sem permissão para cancelar NFC-e.");
        try
        {
            var reason = FiscalDocument.ValidateJustification(justification);
            var settings = RequireSettings();
            var context = await LoadContextAsync(saleId, ct, allowCancelledSale: true);
            var document = await db.FiscalDocuments.SingleOrDefaultAsync(d => d.SaleId == saleId, ct)
                ?? throw new DomainException("Esta venda não tem NFC-e.");
            if (document.Status != FiscalDocumentStatus.Authorized)
                throw new DomainException("Só é possível cancelar NFC-e autorizada. Transmita as pendentes antes.");

            var deadline = document.AuthorizedAt!.Value.AddMinutes(settings.CancellationWindowMinutes);
            if (clock.GetUtcNow() > deadline)
                throw new DomainException($"Prazo de cancelamento ({settings.CancellationWindowMinutes} min) encerrado. Procure o contador.");

            var result = await gateway.CancelAsync(document.AccessKey, document.Protocol!, context.Emitter.Cnpj, reason, settings,
                context.Emitter.Uf, ct);
            if (result.Outcome != SefazOutcome.Authorized)
                throw new DomainException($"SEFAZ não cancelou a nota: {result.StatusCode} - {result.Message}");

            document.MarkCancelled(result.Protocol ?? "", reason, clock.GetUtcNow());
            db.AuditEntries.Add(new AuditEntry("nfce.cancelled", userId, null, document.Id, $"{document.AccessKey} {reason}", clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return Result<NfceInfo>.Ok(ToInfo(document, context.Sale));
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<NfceInfo>.Fail(ex.Message);
        }
    }

    public async Task<NfceInfo?> GetBySaleAsync(Guid saleId, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.SaleId == saleId, ct);
        if (document is null) return null;
        var sale = await db.Sales.AsNoTracking().SingleAsync(s => s.Id == saleId, ct);
        return ToInfo(document, sale);
    }

    public async Task<IReadOnlyList<NfceInfo>> ListRecentAsync(int take = 100, CancellationToken ct = default)
    {
        var rows = await db.FiscalDocuments.AsNoTracking()
            .OrderByDescending(d => d.CreatedAt).Take(take)
            .Join(db.Sales, d => d.SaleId, s => s.Id, (d, s) => new { d, s })
            .ToListAsync(ct);
        return rows.Select(r => ToInfo(r.d, r.s)).ToList();
    }

    public Task<SefazResult> ServiceStatusAsync(CancellationToken ct = default) =>
        ServiceStatusInternalAsync(ct);

    // ----------------------------------------------------------------------------------------------------

    private async Task<SefazResult> ServiceStatusInternalAsync(CancellationToken ct)
    {
        var settings = RequireSettings();
        var uf = await db.Companies.Select(c => c.Address!.Uf).FirstOrDefaultAsync(ct)
            ?? throw new DomainException("Informe a UF da empresa.");
        return await gateway.ServiceStatusAsync(settings, uf, ct);
    }

    private NfceSettings RequireSettings()
    {
        var settings = settingsStore.Load();
        if (!settings.Enabled) throw new DomainException("A emissão de NFC-e está desativada neste computador.");
        if (!settings.IsComplete) throw new DomainException("Configure o certificado digital e o CSC antes de emitir NFC-e.");
        return settings;
    }

    private sealed record EmissionContext(Sale Sale, Guid StoreId, NfceEmitter Emitter, IReadOnlyList<NfceItem> Items);

    private async Task<EmissionContext> LoadContextAsync(Guid saleId, CancellationToken ct, bool allowCancelledSale = false)
    {
        var sale = await db.Sales.Include(s => s.Items).Include(s => s.Payments).SingleOrDefaultAsync(s => s.Id == saleId, ct)
            ?? throw new DomainException("Venda não encontrada.");
        if (sale.Status == SaleStatus.Draft || (!allowCancelledSale && sale.Status == SaleStatus.Cancelled))
            throw new DomainException("A venda não está concluída.");

        var header = await (
            from t in db.Terminals
            join s in db.Stores on t.StoreId equals s.Id
            join c in db.Companies on s.CompanyId equals c.Id
            where t.Id == sale.TerminalId
            select new { StoreId = s.Id, Company = c }).SingleAsync(ct);
        var company = header.Company;
        if (!company.IsFiscallyConfigured || company.Address is null)
            throw new DomainException("Complete os dados fiscais da empresa (retaguarda › Empresa) antes de emitir NFC-e.");

        var missing = sale.Items.Where(i => !i.HasFiscalData).Select(i => i.Description).ToList();
        if (missing.Count > 0)
            throw new DomainException($"Itens sem dados fiscais (NCM/tributação): {string.Join(", ", missing)}.");

        var productIds = sale.Items.Select(i => i.ProductId).ToList();
        var codes = await db.ProductBarcodes.AsNoTracking().Where(b => productIds.Contains(b.ProductId))
            .GroupBy(b => b.ProductId).Select(g => new { g.Key, Code = g.Min(b => b.Code) }).ToDictionaryAsync(x => x.Key, x => x.Code, ct);

        var a = company.Address;
        var emitter = new NfceEmitter(company.Cnpj!, company.LegalName, company.TradeName, company.StateRegistration!, company.TaxRegime,
            a.Street, a.Number, a.Complement, a.District, a.CityIbgeCode, a.City, a.Uf, a.ZipCode, company.Phone);

        var items = sale.Items.OrderBy(i => i.LineNumber).Select(i =>
        {
            codes.TryGetValue(i.ProductId, out var barcode);
            return new NfceItem(i.LineNumber, barcode ?? i.ProductId.ToString("N")[..12].ToUpperInvariant(),
                Gtin.IsValid(barcode) ? barcode : null, i.Description, i.Ncm!, i.Cest, i.Cfop!, i.Unit,
                i.Quantity, i.UnitPrice, i.GrossTotal, i.Discount, i.Origin, i.Csosn, i.IcmsCst, i.IcmsRate,
                i.PisCst!, i.PisRate, i.CofinsCst!, i.CofinsRate);
        }).ToList();

        return new EmissionContext(sale, header.StoreId, emitter, items);
    }

    /// <summary>Reserva o próximo número da série. Concorrência entre caixas é resolvida pelo token da série.</summary>
    private async Task<FiscalDocument> ReserveDocumentAsync(EmissionContext context, NfceSettings settings, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var series = await db.FiscalSeries.SingleOrDefaultAsync(s => s.StoreId == context.StoreId
                && s.Environment == settings.Environment && s.Series == settings.Series, ct);
            if (series is null)
            {
                series = new FiscalSeries(context.StoreId, settings.Environment, settings.Series);
                db.FiscalSeries.Add(series);
            }

            var document = new FiscalDocument(context.Sale.Id, context.StoreId, settings.Environment, settings.Series,
                series.Reserve(), clock.GetUtcNow());
            db.FiscalDocuments.Add(document);
            try
            {
                await db.SaveChangesAsync(ct);
                return document;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                // Outro caixa reservou ao mesmo tempo (ou a venda já ganhou documento em paralelo): recarrega e tenta de novo.
                db.ResetChangeTracker();
                if (await db.FiscalDocuments.SingleOrDefaultAsync(d => d.SaleId == context.Sale.Id, ct) is { } existing)
                    return existing;
            }
        }
    }

    private async Task TransmitAsync(FiscalDocument document, EmissionContext context, NfceSettings settings, CancellationToken ct)
    {
        var uf = context.Emitter.Uf;

        // 1) Já enviada antes sem resposta, ou emitida em contingência: consulta antes de reenviar.
        if (document.Status is FiscalDocumentStatus.Unknown or FiscalDocumentStatus.Contingency && document.AccessKey.Length == 44)
        {
            var query = await gateway.QueryAsync(document.AccessKey, settings, uf, ct);
            if (await ApplyFinalAsync(document, query, ct)) return;
            if (query.Outcome is SefazOutcome.Unavailable or SefazOutcome.Unknown)
            {
                document.MarkUnknown(query.Message);
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        // 2) Monta (ou reaproveita) o XML assinado. Contingência é transmitida exatamente como foi emitida.
        if (document.SignedXml is null || document.Status == FiscalDocumentStatus.Rejected)
            Sign(document, context, settings, FiscalEmissionType.Normal, null);

        document.RegisterAttempt(clock.GetUtcNow());
        await db.SaveChangesAsync(ct); // o XML assinado fica gravado antes de sair do computador

        var result = await gateway.AuthorizeAsync(document.SignedXml!, settings, uf, ct);
        if (result.Outcome == SefazOutcome.Rejected && result.StatusCode == DuplicateStatusCode)
            result = await gateway.QueryAsync(document.AccessKey, settings, uf, ct);

        if (await ApplyFinalAsync(document, result, ct)) return;

        switch (result.Outcome)
        {
            case SefazOutcome.Unavailable when document.Status != FiscalDocumentStatus.Contingency:
                // Sem SEFAZ: emite em contingência offline (nova chave, tpEmis 9) para não parar a venda.
                Sign(document, context, settings, FiscalEmissionType.OfflineContingency, result.Message);
                break;
            case SefazOutcome.Unavailable:
            case SefazOutcome.Unknown:
                document.MarkUnknown(result.Message);
                break;
            case SefazOutcome.NotFound:
                document.MarkUnknown("SEFAZ não localizou a nota; será reenviada.");
                break;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Aplica autorização, rejeição ou denegação. Retorna true se o resultado foi conclusivo.</summary>
    private async Task<bool> ApplyFinalAsync(FiscalDocument document, SefazResult result, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        switch (result.Outcome)
        {
            case SefazOutcome.Authorized:
                document.MarkAuthorized(result.Protocol ?? "", result.StatusCode, result.Message, result.AuthorizedXml ?? document.SignedXml!,
                    result.ProcessedAt ?? now);
                break;
            case SefazOutcome.Denied:
                document.MarkDenied(result.StatusCode, result.Message, result.Protocol);
                break;
            case SefazOutcome.Rejected:
                document.MarkRejected(result.StatusCode, result.Message);
                break;
            default:
                return false;
        }
        db.AuditEntries.Add(new AuditEntry($"nfce.{document.Status.ToString().ToLowerInvariant()}", null, null, document.Id,
            $"{result.StatusCode} {result.Message}", now));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private void Sign(FiscalDocument document, EmissionContext context, NfceSettings settings, FiscalEmissionType type, string? reason)
    {
        var issuedAt = clock.GetUtcNow();
        var randomCode = AccessKey.NewRandomCode(document.Number);
        var key = AccessKey.Build(context.Emitter.Uf, issuedAt, context.Emitter.Cnpj, document.Model, document.Series, document.Number,
            type, randomCode);
        var sale = context.Sale;
        var draft = new NfceDraft(settings.Environment, type, key, randomCode, document.Series, document.Number, issuedAt, reason,
            context.Emitter, sale.CustomerDocument, sale.CustomerName, context.Items,
            sale.Payments.Select(p => new NfcePayment(p.Method, p.Amount, p.IsIntegrated, p.AcquirerCnpj, p.CardBrand, p.AuthorizationCode)).ToList(), sale.ChangeAmount,
            $"Venda nº {sale.Number}");

        var signed = gateway.BuildAndSign(draft, settings);
        document.SetSignedXml(key, type, issuedAt, signed.Xml, signed.QrCodeUrl, signed.ConsultationUrl, reason);
    }

    private static NfceInfo ToInfo(FiscalDocument d, Sale s) =>
        new(d.Id, d.SaleId, s.Number, d.Series, d.Number, d.Environment, d.EmissionType, d.Status, d.AccessKey, d.Protocol,
            d.StatusCode, d.StatusMessage, d.IssuedAt, d.AuthorizedAt, d.QrCodeUrl, d.ConsultationUrl, s.Total, d.Attempts);
}

/// <summary>GTIN (EAN-8/12/13/14) com dígito verificador válido; outros códigos internos vão como "SEM GTIN".</summary>
public static class Gtin
{
    public static bool IsValid(string? code)
    {
        if (code is null || code.Length is not (8 or 12 or 13 or 14) || !code.All(char.IsAsciiDigit)) return false;
        // Pesos 3 e 1 alternados, da direita para a esquerda, sem contar o dígito verificador.
        var sum = 0;
        var weight = 3;
        for (var i = code.Length - 2; i >= 0; i--)
        {
            sum += (code[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        return (10 - sum % 10) % 10 == code[^1] - '0';
    }
}
