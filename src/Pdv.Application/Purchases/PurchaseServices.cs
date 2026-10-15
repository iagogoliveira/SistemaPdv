using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Catalog;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;
using Pdv.Domain.Purchases;

namespace Pdv.Application.Purchases;

public sealed record SupplierModel(Guid? Id, string Name, string? Document, string? Phone, string? Email, bool IsActive = true);

public sealed class SupplierService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    public async Task<IReadOnlyList<SupplierModel>> ListAsync(string? term = null, bool includeInactive = false, CancellationToken ct = default)
    {
        var query = db.Suppliers.AsNoTracking();
        if (!includeInactive) query = query.Where(s => s.IsActive);
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim().ToLowerInvariant();
            var digits = TaxDocuments.Normalize(term);
            query = query.Where(s => s.Name.ToLower().Contains(t) || (digits.Length > 0 && s.Document == digits));
        }
        return await query.OrderBy(s => s.Name)
            .Select(s => new SupplierModel(s.Id, s.Name, s.Document, s.Phone, s.Email, s.IsActive)).ToListAsync(ct);
    }

    public async Task<Result<Guid>> SaveAsync(Guid actorId, SupplierModel m, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageProducts, ct))
            return Result<Guid>.Fail("Usuário sem permissão para cadastrar fornecedores.");
        try
        {
            Supplier supplier;
            if (m.Id is { } id)
            {
                supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new DomainException("Fornecedor não encontrado.");
                supplier.Update(m.Name, m.Document, m.Phone, m.Email);
            }
            else
            {
                supplier = new Supplier(m.Name, m.Document, m.Phone, m.Email);
                db.Suppliers.Add(supplier);
            }
            if (supplier.Document is { } doc && await db.Suppliers.AnyAsync(s => s.Document == doc && s.Id != supplier.Id, ct))
                throw new DomainException("Já existe fornecedor com este CNPJ/CPF.");

            db.AuditEntries.Add(new AuditEntry(m.Id is null ? "supplier.created" : "supplier.updated", actorId, null, supplier.Id, supplier.Name, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Ok(supplier.Id);
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail(ex.Message);
        }
    }

    public async Task<Result<Guid>> SetActiveAsync(Guid actorId, Guid supplierId, bool active, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageProducts, ct))
            return Result<Guid>.Fail("Usuário sem permissão para cadastrar fornecedores.");
        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null) return Result<Guid>.Fail("Fornecedor não encontrado.");
        if (active) supplier.Activate(); else supplier.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result<Guid>.Ok(supplier.Id);
    }
}

/// <summary>Linha da entrada: o que veio do fornecedor e a qual produto da loja corresponde.</summary>
public sealed record PurchaseLine(
    int LineNumber, string? SupplierCode, string? Gtin, string Description, string? Ncm, string Unit,
    decimal SupplierQuantity, decimal LineTotal, Guid? ProductId, string? ProductName, decimal ConversionFactor = 1)
{
    public decimal StockQuantity => Money.RoundQuantity(SupplierQuantity * ConversionFactor);

    /// <summary>Custo por unidade de estoque (4 casas).</summary>
    public decimal UnitCost => StockQuantity == 0 ? 0 : Math.Round(LineTotal / StockQuantity, 4, MidpointRounding.AwayFromZero);

    public bool IsLinked => ProductId is not null;
}

public sealed record PurchaseDraft(
    Guid? SupplierId, string SupplierName, string? SupplierDocument, string? InvoiceNumber, string? InvoiceSeries,
    string? AccessKey, DateTimeOffset? IssuedAt, IReadOnlyList<PurchaseLine> Lines, IReadOnlyList<string> Warnings)
{
    public decimal Total => Lines.Sum(l => l.LineTotal);
    public int UnlinkedCount => Lines.Count(l => !l.IsLinked);
}

public sealed record PurchaseListItem(Guid Id, DateTimeOffset CreatedAt, string SupplierName, string? InvoiceNumber, int Items, decimal Total,
    PurchaseStatus Status);

/// <summary>Entrada de mercadorias: manual ou a partir do XML da NF-e do fornecedor.</summary>
public sealed class PurchaseService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    /// <summary>Lê o XML e prepara a entrada, sem gravar nada. Itens já conhecidos vêm vinculados.</summary>
    public async Task<Result<PurchaseDraft>> PreviewXmlAsync(string xml, CancellationToken ct = default)
    {
        try
        {
            var invoice = NfeXmlParser.Parse(xml);
            var warnings = new List<string>();

            if (await db.Purchases.AnyAsync(p => p.AccessKey == invoice.AccessKey, ct))
                throw new DomainException($"A NF-e {invoice.Number} deste fornecedor já foi lançada.");

            var companyCnpj = await db.Companies.Select(c => c.Cnpj).FirstOrDefaultAsync(ct);
            if (companyCnpj is not null && invoice.RecipientDocument is not null && invoice.RecipientDocument != companyCnpj)
                warnings.Add($"A nota foi emitida para outro destinatário ({TaxDocuments.Format(invoice.RecipientDocument)}), não para a sua empresa.");

            var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Document == invoice.SupplierDocument, ct);
            if (supplier is null) warnings.Add($"Fornecedor {invoice.SupplierName} será cadastrado automaticamente.");

            var links = supplier is null
                ? []
                : await db.SupplierProducts.AsNoTracking().Where(l => l.SupplierId == supplier.Id).ToDictionaryAsync(l => l.SupplierCode, ct);
            var gtins = invoice.Items.Select(i => i.Gtin).OfType<string>().Distinct().ToList();
            var byGtin = await db.ProductBarcodes.AsNoTracking().Where(b => gtins.Contains(b.Code))
                .ToDictionaryAsync(b => b.Code, b => b.ProductId, ct);
            var names = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);

            var lines = invoice.Items.Select(i =>
            {
                Guid? productId = null;
                var factor = 1m;
                if (links.TryGetValue(i.SupplierCode, out var link)) { productId = link.ProductId; factor = link.ConversionFactor; }
                else if (i.Gtin is not null && byGtin.TryGetValue(i.Gtin, out var pid)) productId = pid;
                return new PurchaseLine(i.LineNumber, i.SupplierCode, i.Gtin, i.Description, i.Ncm, i.Unit, i.Quantity, i.NetTotal,
                    productId, productId is { } id && names.TryGetValue(id, out var n) ? n : null, factor);
            }).ToList();

            return Result<PurchaseDraft>.Ok(new PurchaseDraft(supplier?.Id, supplier?.Name ?? invoice.SupplierName, invoice.SupplierDocument,
                invoice.Number, invoice.Series, invoice.AccessKey, invoice.IssuedAt, lines, warnings));
        }
        catch (DomainException ex)
        {
            return Result<PurchaseDraft>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Grava e confirma a entrada numa única transação: fornecedor (se novo), vínculos aprendidos,
    /// movimentos de estoque, custo atualizado e NCM preenchido nos produtos que ainda não tinham.
    /// </summary>
    public async Task<Result<Guid>> ConfirmAsync(Guid actorId, PurchaseDraft draft, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.AdjustStock, ct))
            return Result<Guid>.Fail("Usuário sem permissão para dar entrada de mercadorias.");
        try
        {
            if (draft.Lines.Count == 0) throw new DomainException("A entrada não possui itens.");
            var unlinked = draft.Lines.Where(l => !l.IsLinked).Select(l => l.Description).ToList();
            if (unlinked.Count > 0) throw new DomainException($"Vincule todos os itens a produtos da loja: {string.Join(", ", unlinked)}.");
            if (draft.AccessKey is { } key && await db.Purchases.AnyAsync(p => p.AccessKey == key, ct))
                throw new DomainException("Esta NF-e já foi lançada.");

            var now = clock.GetUtcNow();
            var supplierId = draft.SupplierId ?? await FindOrCreateSupplierAsync(draft, ct);
            var purchase = new Purchase(supplierId, draft.InvoiceNumber, draft.InvoiceSeries, draft.AccessKey, draft.IssuedAt, actorId, now);

            var productIds = draft.Lines.Select(l => l.ProductId!.Value).Distinct().ToList();
            var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var links = await db.SupplierProducts.Where(l => l.SupplierId == supplierId).ToDictionaryAsync(l => l.SupplierCode, ct);

            foreach (var line in draft.Lines)
            {
                var product = products.GetValueOrDefault(line.ProductId!.Value) ?? throw new DomainException($"Produto de \"{line.Description}\" não encontrado.");
                purchase.AddItem(product.Id, line.StockQuantity, line.UnitCost, line.SupplierCode, line.Description);
                product.ChangePrices(product.SalePrice, line.UnitCost);
                if (product.Ncm is null && line.Ncm is not null)
                    product.SetFiscalData(line.Ncm, product.Cest, product.Origin, product.TaxProfileId);

                if (!string.IsNullOrWhiteSpace(line.SupplierCode))
                {
                    if (links.TryGetValue(line.SupplierCode, out var link)) link.Relink(product.Id, line.ConversionFactor);
                    else
                    {
                        link = new SupplierProduct(supplierId, line.SupplierCode, product.Id, line.ConversionFactor);
                        links[line.SupplierCode] = link;
                        db.SupplierProducts.Add(link);
                    }
                }
            }

            purchase.Confirm(actorId, now);
            db.Purchases.Add(purchase);
            foreach (var item in purchase.Items)
                db.StockMovements.Add(StockMovement.ForPurchase(item.ProductId, item.Quantity, purchase.Id, actorId, now,
                    draft.InvoiceNumber is null ? "Entrada manual" : $"NF {draft.InvoiceNumber}"));
            db.AuditEntries.Add(new AuditEntry("purchase.confirmed", actorId, null, purchase.Id,
                $"{draft.SupplierName} NF {draft.InvoiceNumber} {purchase.Total:N2}", now));
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Ok(purchase.Id);
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail(ex.Message);
        }
        catch (DbUpdateException)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail("Esta NF-e já foi lançada (ou houve conflito ao gravar). Atualize e tente de novo.");
        }
    }

    public async Task<IReadOnlyList<PurchaseListItem>> ListRecentAsync(int take = 100, CancellationToken ct = default) =>
        await db.Purchases.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt).Take(take)
            .Join(db.Suppliers, p => p.SupplierId, s => s.Id, (p, s) => new PurchaseListItem(p.Id, p.CreatedAt, s.Name, p.InvoiceNumber,
                p.Items.Count, p.Items.Sum(i => i.Total), p.Status))
            .ToListAsync(ct);

    private async Task<Guid> FindOrCreateSupplierAsync(PurchaseDraft draft, CancellationToken ct)
    {
        if (draft.SupplierDocument is { Length: > 0 } doc && await db.Suppliers.FirstOrDefaultAsync(s => s.Document == doc, ct) is { } existing)
            return existing.Id;
        var supplier = new Supplier(draft.SupplierName, draft.SupplierDocument);
        db.Suppliers.Add(supplier);
        return supplier.Id;
    }
}
