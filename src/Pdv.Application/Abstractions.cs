using Microsoft.EntityFrameworkCore;
using Pdv.Domain.Auditing;
using Pdv.Domain.Cash;
using Pdv.Domain.Catalog;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;
using Pdv.Domain.Payments;
using Pdv.Domain.Purchases;
using Pdv.Domain.Sales;
using Pdv.Domain.Stores;

namespace Pdv.Application;

public interface IPdvDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<Store> Stores { get; }
    DbSet<Terminal> Terminals { get; }
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<Category> Categories { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductBarcode> ProductBarcodes { get; }
    DbSet<TaxProfile> TaxProfiles { get; }
    DbSet<FiscalDocument> FiscalDocuments { get; }
    DbSet<FiscalSeries> FiscalSeries { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<CashSession> CashSessions { get; }
    DbSet<CashMovement> CashMovements { get; }
    DbSet<Sale> Sales { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<Purchase> Purchases { get; }
    DbSet<SupplierProduct> SupplierProducts { get; }
    DbSet<SaleReturn> SaleReturns { get; }
    DbSet<ExchangeVoucher> ExchangeVouchers { get; }
    DbSet<PixCharge> PixCharges { get; }
    DbSet<Domain.Common.AppSetting> AppSettings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Descarta entidades rastreadas após uma falha de gravação.</summary>
    void ResetChangeTracker();
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

/// <summary>Resultado de operações que podem falhar por regra de negócio, sem usar exceções no fluxo da UI.</summary>
public sealed record Result<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;
    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(string error) => new(default, error);
}
