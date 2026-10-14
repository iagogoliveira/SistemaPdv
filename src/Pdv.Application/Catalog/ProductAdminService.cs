using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Catalog;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;

namespace Pdv.Application.Catalog;

public sealed record ProductListItem(Guid Id, string Name, string Unit, string? CategoryName, decimal SalePrice, decimal CostPrice,
    decimal Stock, string Barcodes, bool IsActive, bool HasFiscalData = false)
{
    public const decimal LowStockThreshold = 5;

    public decimal? MarginPercent => SalePrice == 0 ? null : Money.Round((SalePrice - CostPrice) / SalePrice * 100);

    /// <summary>Destaque visual simples enquanto não existe estoque mínimo por produto.</summary>
    public bool IsLowStock => IsActive && Stock <= LowStockThreshold;
}

public sealed record ProductEditModel(Guid? Id, string Name, string Unit, string? CategoryName, decimal SalePrice, decimal CostPrice,
    IReadOnlyList<string> Barcodes, string? Ncm = null, string? Cest = null, ProductOrigin Origin = ProductOrigin.Nacional,
    Guid? TaxProfileId = null);

public sealed record StockMovementListItem(DateTimeOffset OccurredAt, StockMovementType Type, decimal Quantity, string? Reason, string UserName);

public sealed class ProductAdminService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    public async Task<IReadOnlyList<ProductListItem>> ListAsync(string? term, bool includeInactive, int take = 200, CancellationToken ct = default)
    {
        var query = db.Products.AsNoTracking();
        if (!includeInactive) query = query.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            var lower = t.ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(lower) || p.Barcodes.Any(b => b.Code == t));
        }

        var rows = await query.OrderBy(p => p.Name).Take(take)
            .Select(p => new
            {
                p.Id, p.Name, p.Unit, p.SalePrice, p.CostPrice, p.IsActive, p.Ncm,
                Category = db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                Stock = db.StockMovements.Where(m => m.ProductId == p.Id).Sum(m => (decimal?)m.Quantity) ?? 0,
                Codes = p.Barcodes.Select(b => b.Code).ToList(),
            })
            .ToListAsync(ct);

        return rows.Select(r => new ProductListItem(r.Id, r.Name, r.Unit, r.Category, r.SalePrice, r.CostPrice, r.Stock,
            string.Join(", ", r.Codes), r.IsActive, r.Ncm != null)).ToList();
    }

    public async Task<ProductEditModel?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Products.AsNoTracking().Include(x => x.Barcodes).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var category = p.CategoryId is null ? null : await db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        return new ProductEditModel(p.Id, p.Name, p.Unit, category, p.SalePrice, p.CostPrice, p.Barcodes.Select(b => b.Code).ToList(),
            p.Ncm, p.Cest, p.Origin, p.TaxProfileId);
    }

    public async Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken ct = default) =>
        await db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(ct);

    public Task<Result<Guid>> SaveAsync(Guid actorId, ProductEditModel model, CancellationToken ct = default) =>
        RunAsync(actorId, Permissions.ManageProducts, async () =>
        {
            var codes = model.Barcodes.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
            var categoryId = await ResolveCategoryAsync(model.CategoryName, ct);

            Product product;
            if (model.Id is { } id)
            {
                product = await db.Products.Include(p => p.Barcodes).SingleOrDefaultAsync(p => p.Id == id, ct)
                    ?? throw new DomainException("Produto não encontrado.");
                product.Update(model.Name, model.Unit, categoryId);
                product.ChangePrices(model.SalePrice, model.CostPrice);
            }
            else
            {
                product = new Product(model.Name, model.SalePrice, model.CostPrice, model.Unit, categoryId);
                db.Products.Add(product);
            }

            if (model.TaxProfileId is { } profileId)
            {
                var profile = await db.TaxProfiles.SingleOrDefaultAsync(t => t.Id == profileId, ct)
                    ?? throw new DomainException("Perfil tributário não encontrado.");
                if (!profile.IsActive) throw new DomainException("Perfil tributário inativo.");
                profile.EnsureCompatibleWith(await db.Companies.Select(c => c.TaxRegime).FirstAsync(ct));
            }
            product.SetFiscalData(model.Ncm, model.Cest, model.Origin, model.TaxProfileId);

            var conflicts = await db.ProductBarcodes.Where(b => codes.Contains(b.Code) && b.ProductId != product.Id)
                .Select(b => b.Code).ToListAsync(ct);
            if (conflicts.Count > 0) throw new DomainException($"Código já usado em outro produto: {string.Join(", ", conflicts)}");

            foreach (var removed in product.Barcodes.Where(b => !codes.Contains(b.Code)).ToList())
            {
                product.RemoveBarcode(removed.Code);
                db.ProductBarcodes.Remove(removed);
            }
            foreach (var code in codes.Where(c => product.Barcodes.All(b => b.Code != c)))
                product.AddBarcode(code);

            Audit(actorId, model.Id is null ? "product.created" : "product.updated", product.Id, $"{product.Name} {product.SalePrice:N2}");
            return product.Id;
        }, ct);

    public Task<Result<Guid>> SetActiveAsync(Guid actorId, Guid productId, bool active, CancellationToken ct = default) =>
        RunAsync(actorId, Permissions.ManageProducts, async () =>
        {
            var product = await db.Products.SingleOrDefaultAsync(p => p.Id == productId, ct) ?? throw new DomainException("Produto não encontrado.");
            if (active) product.Activate(); else product.Deactivate();
            Audit(actorId, active ? "product.activated" : "product.deactivated", product.Id, product.Name);
            return product.Id;
        }, ct);

    /// <summary>Entrada de mercadoria (compra). Quantidade positiva; custo opcional atualiza o custo do produto.</summary>
    public Task<Result<Guid>> RegisterEntryAsync(Guid actorId, Guid productId, decimal quantity, decimal? unitCost, string? note,
        CancellationToken ct = default) =>
        RunAsync(actorId, Permissions.AdjustStock, async () =>
        {
            var product = await db.Products.SingleOrDefaultAsync(p => p.Id == productId, ct) ?? throw new DomainException("Produto não encontrado.");
            if (unitCost is { } cost) product.ChangePrices(product.SalePrice, cost);
            var movement = StockMovement.ForPurchase(productId, quantity, null, actorId, clock.GetUtcNow(), note);
            db.StockMovements.Add(movement);
            Audit(actorId, "stock.entry", productId, $"{quantity:0.###} {note}");
            return movement.Id;
        }, ct);

    /// <summary>Ajuste para a quantidade contada: gera um movimento com a diferença, nunca altera o histórico.</summary>
    public Task<Result<Guid>> AdjustToCountAsync(Guid actorId, Guid productId, decimal countedQuantity, string reason,
        CancellationToken ct = default) =>
        RunAsync(actorId, Permissions.AdjustStock, async () =>
        {
            if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) throw new DomainException("Produto não encontrado.");
            var current = await db.StockMovements.Where(m => m.ProductId == productId).SumAsync(m => (decimal?)m.Quantity, ct) ?? 0;
            var delta = countedQuantity - current;
            var movement = StockMovement.ForAdjustment(productId, delta, reason, actorId, clock.GetUtcNow());
            db.StockMovements.Add(movement);
            Audit(actorId, AuditActions.StockAdjusted, productId, $"{current:0.###} → {countedQuantity:0.###}: {reason}");
            return movement.Id;
        }, ct);

    public async Task<IReadOnlyList<StockMovementListItem>> ListMovementsAsync(Guid productId, int take = 100, CancellationToken ct = default) =>
        await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId)
            .OrderByDescending(m => m.OccurredAt)
            .Take(take)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new StockMovementListItem(m.OccurredAt, m.Type, m.Quantity, m.Reason, u.DisplayName))
            .ToListAsync(ct);

    private async Task<Guid?> ResolveCategoryAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        var existing = await db.Categories.Where(c => c.Name == trimmed).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;
        var category = new Category(trimmed);
        db.Categories.Add(category);
        return category.Id;
    }

    private void Audit(Guid actorId, string action, Guid referenceId, string details) =>
        db.AuditEntries.Add(new AuditEntry(action, actorId, null, referenceId, details, clock.GetUtcNow()));

    private async Task<Result<Guid>> RunAsync(Guid actorId, string permission, Func<Task<Guid>> operation, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, permission, ct))
            return Result<Guid>.Fail("Usuário sem permissão para esta operação.");
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
        catch (DbUpdateException)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail("Não foi possível gravar: código de barras ou registro duplicado.");
        }
    }
}
