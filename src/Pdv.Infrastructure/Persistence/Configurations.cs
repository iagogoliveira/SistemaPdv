using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pdv.Domain.Auditing;
using Pdv.Domain.Cash;
using Pdv.Domain.Catalog;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;
using Pdv.Domain.Sales;
using Pdv.Domain.Stores;

namespace Pdv.Infrastructure.Persistence;

// Regra geral: chaves estrangeiras com DeleteBehavior.Restrict — histórico não é apagado em cascata.

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.Property(x => x.LegalName).HasMaxLength(150);
        b.Property(x => x.TradeName).HasMaxLength(150);
        b.Property(x => x.Cnpj).HasMaxLength(14);
        b.Property(x => x.StateRegistration).HasMaxLength(14);
        b.Property(x => x.MunicipalRegistration).HasMaxLength(20);
        b.Property(x => x.Phone).HasMaxLength(14);
        b.Ignore(x => x.IsFiscallyConfigured);
        b.OwnsOne(x => x.Address, a =>
        {
            a.Property(x => x.Street).HasMaxLength(60).HasColumnName("AddressStreet");
            a.Property(x => x.Number).HasMaxLength(60).HasColumnName("AddressNumber");
            a.Property(x => x.Complement).HasMaxLength(60).HasColumnName("AddressComplement");
            a.Property(x => x.District).HasMaxLength(60).HasColumnName("AddressDistrict");
            a.Property(x => x.City).HasMaxLength(60).HasColumnName("AddressCity");
            a.Property(x => x.CityIbgeCode).HasMaxLength(7).HasColumnName("AddressCityIbgeCode");
            a.Property(x => x.Uf).HasMaxLength(2).HasColumnName("AddressUf");
            a.Property(x => x.ZipCode).HasMaxLength(8).HasColumnName("AddressZipCode");
        });
        b.HasOne<TaxProfile>().WithMany().HasForeignKey(x => x.DefaultTaxProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TaxProfileConfiguration : IEntityTypeConfiguration<TaxProfile>
{
    public void Configure(EntityTypeBuilder<TaxProfile> b)
    {
        b.Property(x => x.Name).HasMaxLength(80);
        b.Property(x => x.Cfop).HasMaxLength(4);
        b.Property(x => x.Csosn).HasMaxLength(3);
        b.Property(x => x.IcmsCst).HasMaxLength(2);
        b.Property(x => x.PisCst).HasMaxLength(2);
        b.Property(x => x.CofinsCst).HasMaxLength(2);
        b.Property(x => x.IcmsRate).HasPrecision(7, 4);
        b.Property(x => x.PisRate).HasPrecision(7, 4);
        b.Property(x => x.CofinsRate).HasPrecision(7, 4);
        b.Ignore(x => x.UsesCsosn);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TerminalConfiguration : IEntityTypeConfiguration<Terminal>
{
    public void Configure(EntityTypeBuilder<Terminal> b)
    {
        b.Property(x => x.Code).HasMaxLength(10);
        b.Property(x => x.MachineName).HasMaxLength(64);
        b.HasIndex(x => x.MachineName).IsUnique();
        b.HasIndex(x => new { x.StoreId, x.Code }).IsUnique();
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.Property(x => x.Name).HasMaxLength(60);
        b.HasIndex(x => x.Name).IsUnique();
        b.PrimitiveCollection(x => x.Permissions);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(x => x.Username).HasMaxLength(60);
        b.Property(x => x.DisplayName).HasMaxLength(100);
        b.Property(x => x.PasswordHash).HasMaxLength(200);
        b.HasIndex(x => x.Username).IsUnique();
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b) => b.Property(x => x.Name).HasMaxLength(80);
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.Document).HasMaxLength(20);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.Unit).HasMaxLength(6);
        b.Property(x => x.Ncm).HasMaxLength(8);
        b.Property(x => x.Cest).HasMaxLength(7);
        b.Ignore(x => x.HasFiscalData);
        b.HasIndex(x => x.Name);
        b.HasOne<TaxProfile>().WithMany().HasForeignKey(x => x.TaxProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Barcodes).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductBarcodeConfiguration : IEntityTypeConfiguration<ProductBarcode>
{
    public void Configure(EntityTypeBuilder<ProductBarcode> b)
    {
        b.Property(x => x.Code).HasMaxLength(50);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.Reason).HasMaxLength(250);
        b.HasIndex(x => new { x.ProductId, x.OccurredAt });
        b.HasIndex(x => x.ReferenceId);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> b)
    {
        // Token de concorrência: coluna de sistema xmin do PostgreSQL (muda a cada UPDATE da linha).
        b.Property<uint>("Version").IsRowVersion();
        b.Ignore(x => x.ExpectedCash);
        // No máximo um caixa aberto por terminal, garantido pelo banco.
        b.HasIndex(x => x.TerminalId).IsUnique().HasFilter("\"Status\" = 'Open'");
        b.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OpenedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Movements).WithOne().HasForeignKey(x => x.CashSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> b)
    {
        b.Property(x => x.Reason).HasMaxLength(250);
        b.Ignore(x => x.SignedAmount);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.HasIndex(x => x.ClientRequestId).IsUnique();
        b.Property(x => x.Number).HasDefaultValueSql($"nextval('\"{PdvDbContext.SaleNumberSequence}\"')");
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.CompletedAt);
        b.Property(x => x.CancellationReason).HasMaxLength(250);
        b.Property(x => x.CustomerDocument).HasMaxLength(14);
        b.Property(x => x.CustomerName).HasMaxLength(60);
        b.Ignore(x => x.PaidTotal);
        b.Ignore(x => x.NetCashReceived);
        b.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CashSession>().WithMany().HasForeignKey(x => x.CashSessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.DiscountAuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> b)
    {
        b.Property(x => x.Description).HasMaxLength(120);
        b.Property(x => x.Unit).HasMaxLength(6);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.Ncm).HasMaxLength(8);
        b.Property(x => x.Cest).HasMaxLength(7);
        b.Property(x => x.Cfop).HasMaxLength(4);
        b.Property(x => x.Csosn).HasMaxLength(3);
        b.Property(x => x.IcmsCst).HasMaxLength(2);
        b.Property(x => x.PisCst).HasMaxLength(2);
        b.Property(x => x.CofinsCst).HasMaxLength(2);
        b.Property(x => x.IcmsRate).HasPrecision(7, 4);
        b.Property(x => x.PisRate).HasPrecision(7, 4);
        b.Property(x => x.CofinsRate).HasPrecision(7, 4);
        b.Ignore(x => x.HasFiscalData);
        b.HasIndex(x => new { x.SaleId, x.LineNumber }).IsUnique();
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.Property(x => x.Action).HasMaxLength(60);
        b.Property(x => x.Details).HasMaxLength(500);
        b.HasIndex(x => x.OccurredAt);
    }
}
