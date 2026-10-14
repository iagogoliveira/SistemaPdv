using Microsoft.EntityFrameworkCore;

namespace Pdv.Application.Catalog;

public sealed record ProductLookupItem(Guid ProductId, string Name, string Unit, decimal SalePrice, string? Barcode);

public sealed class ProductLookupService(IPdvDbContext db)
{
    public async Task<ProductLookupItem?> FindByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        var code = barcode.Trim();
        return await db.ProductBarcodes.AsNoTracking()
            .Where(b => b.Code == code)
            .Join(db.Products.Where(p => p.IsActive), b => b.ProductId, p => p.Id,
                (b, p) => new ProductLookupItem(p.Id, p.Name, p.Unit, p.SalePrice, b.Code))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ProductLookupItem>> SearchAsync(string term, int take = 30, CancellationToken ct = default)
    {
        var pattern = $"%{term.Trim()}%";
        return await db.Products.AsNoTracking()
            .Where(p => p.IsActive && EF.Functions.Like(p.Name, pattern))
            .OrderBy(p => p.Name)
            .Take(take)
            .Select(p => new ProductLookupItem(p.Id, p.Name, p.Unit, p.SalePrice, null))
            .ToListAsync(ct);
    }

    public async Task<decimal> GetStockAsync(Guid productId, CancellationToken ct = default) =>
        await db.StockMovements.Where(m => m.ProductId == productId).SumAsync(m => (decimal?)m.Quantity, ct) ?? 0;
}
