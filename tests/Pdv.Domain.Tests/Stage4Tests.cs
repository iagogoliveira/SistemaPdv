using Pdv.Domain.Catalog;
using Pdv.Domain.Common;
using Pdv.Domain.Purchases;
using Pdv.Domain.Sales;

namespace Pdv.Domain.Tests;

public class SaleReturnTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<int, (decimal, decimal)> Nothing = [];

    /// <summary>3 cadernos a 10,00 com 1,00 de desconto: pago 29,00 (9,6666... por unidade).</summary>
    private static Sale CompletedSale()
    {
        var sale = new Sale(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        sale.AddItem(Guid.NewGuid(), "Caderno", "UN", 3, 10m, discount: 1m);
        sale.AddItem(Guid.NewGuid(), "Caneta", "UN", 2, 2.50m);
        sale.SetDiscountAuthorizer(Guid.NewGuid());
        sale.AddPayment(PaymentMethod.Cash, 34m);
        sale.Complete(Now);
        return sale;
    }

    [Fact]
    public void Partial_return_is_proportional_and_last_unit_takes_exact_rest()
    {
        var sale = CompletedSale();

        var first = SaleReturn.Create(sale, new Dictionary<int, decimal> { [1] = 1 }, Nothing, RefundMethod.Cash, "Defeito",
            Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Equal(9.67m, first.Total);

        var already = new Dictionary<int, (decimal, decimal)> { [1] = (1m, 9.67m) };
        var rest = SaleReturn.Create(sale, new Dictionary<int, decimal> { [1] = 2 }, already, RefundMethod.Cash, "Defeito",
            Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Equal(19.33m, rest.Total);   // 29,00 − 9,67: nada sobra nem falta
    }

    [Fact]
    public void Cannot_return_more_than_sold_or_without_reason()
    {
        var sale = CompletedSale();
        var already = new Dictionary<int, (decimal, decimal)> { [2] = (2m, 5m) };

        Assert.Throws<DomainException>(() => SaleReturn.Create(sale, new Dictionary<int, decimal> { [2] = 1 }, already,
            RefundMethod.Cash, "Troca", Guid.NewGuid(), Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => SaleReturn.Create(sale, new Dictionary<int, decimal> { [1] = 4 }, Nothing,
            RefundMethod.Cash, "Troca", Guid.NewGuid(), Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => SaleReturn.Create(sale, new Dictionary<int, decimal> { [1] = 1 }, Nothing,
            RefundMethod.Cash, " ", Guid.NewGuid(), Guid.NewGuid(), Now));
    }

    [Fact]
    public void Cancelled_sale_cannot_be_returned()
    {
        var sale = CompletedSale();
        sale.Cancel("Cliente desistiu", Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => SaleReturn.Create(sale, new Dictionary<int, decimal> { [1] = 1 }, Nothing,
            RefundMethod.Cash, "Troca", Guid.NewGuid(), Guid.NewGuid(), Now));
    }
}

public class ExchangeVoucherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Voucher_can_be_used_in_parts_until_balance_is_zero()
    {
        var voucher = new ExchangeVoucher(30m, Now, Now.AddDays(90));
        Assert.Equal(8, voucher.Code.Length);
        Assert.DoesNotContain(voucher.Code, c => c is '0' or 'O' or '1' or 'I');

        voucher.Redeem(20m, Now);
        voucher.Redeem(10m, Now);
        Assert.Equal(0m, voucher.Balance);
        Assert.Throws<DomainException>(() => voucher.Redeem(0.01m, Now));
    }

    [Fact]
    public void Expired_voucher_is_refused()
    {
        var voucher = new ExchangeVoucher(30m, Now, Now.AddDays(90));
        Assert.Throws<DomainException>(() => voucher.Redeem(5m, Now.AddDays(91)));
    }

    [Fact]
    public void Code_normalization_ignores_dashes_and_case() =>
        Assert.Equal("ABCD2345", ExchangeVoucher.NormalizeCode(" abcd-2345 "));
}

public class PurchaseAndCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Purchase_confirms_once_and_needs_items()
    {
        var purchase = new Purchase(Guid.NewGuid(), "123", "1", null, null, Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => purchase.Confirm(Guid.NewGuid(), Now));

        var item = purchase.AddItem(Guid.NewGuid(), 24, 1.23456m);
        Assert.Equal(1.2346m, item.UnitCost);
        Assert.Equal(29.63m, item.Total);

        purchase.Confirm(Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => purchase.Confirm(Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => purchase.AddItem(Guid.NewGuid(), 1, 1));
    }

    [Fact]
    public void Purchase_rejects_invalid_access_key() =>
        Assert.Throws<DomainException>(() => new Purchase(Guid.NewGuid(), "1", "1", "123", null, Guid.NewGuid(), Now));

    [Fact]
    public void Supplier_product_link_needs_positive_factor() =>
        Assert.Throws<DomainException>(() => new SupplierProduct(Guid.NewGuid(), "ABC", Guid.NewGuid(), 0));

    [Fact]
    public void Supplier_document_is_validated()
    {
        Assert.Throws<DomainException>(() => new Supplier("Distribuidora", "11.222.333/0001-82"));
        Assert.Equal("11222333000181", new Supplier("Distribuidora", "11.222.333/0001-81").Document);
    }

    [Fact]
    public void Minimum_stock_cannot_be_negative()
    {
        var product = new Product("Caderno", 10, 5);
        Assert.Throws<DomainException>(() => product.SetMinimumStock(-1));
        product.SetMinimumStock(12.5m);
        Assert.Equal(12.5m, product.MinimumStock);
    }
}
