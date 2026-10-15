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
        var text = term.Trim().ToLowerInvariant();
        return await db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.Name.ToLower().Contains(text))
            .OrderBy(p => p.Name)
            .Take(take)
            .Select(p => new ProductLookupItem(p.Id, p.Name, p.Unit, p.SalePrice, null))
            .ToListAsync(ct);
    }

    /// <summary>Consulta de preço no caixa: código de barras ou parte do nome (até 10 resultados).</summary>
    public async Task<IReadOnlyList<(ProductLookupItem Product, decimal Stock)>> PriceCheckAsync(string term, CancellationToken ct = default)
    {
        var found = await FindByBarcodeAsync(term, ct) is { } byCode ? [byCode] : await SearchAsync(term, 10, ct);
        var ids = found.Select(f => f.ProductId).ToList();
        var stock = await db.StockMovements.Where(m => ids.Contains(m.ProductId)).GroupBy(m => m.ProductId)
            .Select(g => new { g.Key, Total = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        return found.Select(f => (f, stock.GetValueOrDefault(f.ProductId))).ToList();
    }

    public async Task<decimal> GetStockAsync(Guid productId, CancellationToken ct = default) =>
        await db.StockMovements.Where(m => m.ProductId == productId).SumAsync(m => (decimal?)m.Quantity, ct) ?? 0;
}
