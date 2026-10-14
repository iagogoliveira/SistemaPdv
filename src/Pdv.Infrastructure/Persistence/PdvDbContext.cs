using Microsoft.EntityFrameworkCore;
using Pdv.Application;
using Pdv.Domain.Auditing;
using Pdv.Domain.Cash;
using Pdv.Domain.Catalog;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;
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

    public const string SaleNumberSequence = "SaleNumbers";

    public void ResetChangeTracker() => ChangeTracker.Clear();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Valores monetários: decimal(18,2). Quantidades sobrescrevem para (18,3) nas configurações.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
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
