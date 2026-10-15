using Microsoft.EntityFrameworkCore;
using Pdv.Application;
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

namespace Pdv.Infrastructure.Persistence;

public sealed class PdvDbContext(DbContextOptions<PdvDbContext> options) : DbContext(options), IPdvDbContext
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<TaxProfile> TaxProfiles => Set<TaxProfile>();
    public DbSet<FiscalDocument> FiscalDocuments => Set<FiscalDocument>();
    public DbSet<FiscalSeries> FiscalSeries => Set<FiscalSeries>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<SupplierProduct> SupplierProducts => Set<SupplierProduct>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<ExchangeVoucher> ExchangeVouchers => Set<ExchangeVoucher>();
    public DbSet<PixCharge> PixCharges => Set<PixCharge>();
    public DbSet<TaxApproximation> TaxApproximations => Set<TaxApproximation>();
    public DbSet<NumberVoid> NumberVoids => Set<NumberVoid>();
    public DbSet<Domain.Common.AppSetting> AppSettings => Set<Domain.Common.AppSetting>();

    public const string SaleNumberSequence = "SaleNumbers";

    public void ResetChangeTracker() => ChangeTracker.Clear();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Valores monetários: decimal(18,2). Quantidades sobrescrevem para (18,3) nas configurações.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
        // PostgreSQL (timestamptz) só aceita gravar em UTC: datas com fuso (ex.: XML de fornecedor, -03:00) são convertidas.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(SaleNumberSequence);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PdvDbContext).Assembly);

        // IDs são gerados no domínio (Guid v7). Sem isso, filhos novos adicionados a um agregado já
        // rastreado (ex.: CashMovement em CashSession) seriam tratados como UPDATE em vez de INSERT.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            entity.FindProperty(nameof(Domain.Common.Entity.Id))?.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
    }
}

internal sealed class UtcDateTimeOffsetConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);
