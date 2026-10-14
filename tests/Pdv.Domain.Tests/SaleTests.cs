using Pdv.Domain.Common;
using Pdv.Domain.Sales;

namespace Pdv.Domain.Tests;

public class SaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static Sale NewSale() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);

    [Fact]
    public void Totals_round_each_line_half_away_from_zero()
    {
        var sale = NewSale();
        sale.AddItem(Guid.NewGuid(), "Granel", "KG", 0.335m, 9.99m); // 3,34665 → 3,35
        sale.AddItem(Guid.NewGuid(), "Caneta", "UN", 3, 2.50m);

        Assert.Equal(3.35m, sale.Items.First().GrossTotal);
        Assert.Equal(10.85m, sale.Total);
    }

    [Fact]
    public void Item_keeps_snapshot_of_description_and_price()
    {
        var sale = NewSale();
        var item = sale.AddItem(Guid.NewGuid(), "Caderno", "UN", 2, 14.90m, discount: 1.80m);

        Assert.Equal("Caderno", item.Description);
        Assert.Equal(14.90m, item.UnitPrice);
        Assert.Equal(28.00m, item.Total);
        Assert.Equal(1.80m, sale.DiscountTotal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30)]
    public void Discount_must_be_between_zero_and_gross_total(decimal discount)
    {
        var sale = NewSale();
        Assert.Throws<DomainException>(() => sale.AddItem(Guid.NewGuid(), "Caderno", "UN", 2, 14.90m, discount));
    }

    [Fact]
    public void Discount_requires_authorizer_to_complete()
    {
        var sale = NewSale();
        sale.AddItem(Guid.NewGuid(), "Caderno", "UN", 1, 10m, discount: 1m);
        sale.AddPayment(PaymentMethod.Cash, 9m);

        Assert.Throws<DomainException>(() => sale.Complete(Now));

        sale.SetDiscountAuthorizer(Guid.NewGuid());
        sale.Complete(Now);
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    [Fact]
    public void Cannot_complete_with_insufficient_payment()
    {
        var sale = NewSale();
        sale.AddItem(Guid.NewGuid(), "Pilha", "UN", 1, 9.99m);
        sale.AddPayment(PaymentMethod.Pix, 5m);

        Assert.Throws<DomainException>(() => sale.Complete(Now));
        Assert.Equal(SaleStatus.Draft, sale.Status);
    }

    [Fact]
    public void Change_comes_only_from_cash()
    {
        var sale = NewSale();
        sale.AddItem(Guid.NewGuid(), "Pilha", "UN", 1, 9.99m);
        sale.AddPayment(PaymentMethod.CreditCard, 20m);

        Assert.Throws<DomainException>(() => sale.Complete(Now));
    }

    [Fact]
    public void Mixed_payment_computes_change_and_net_cash()
    {
        var sale = NewSale();
        sale.AddItem(Guid.NewGuid(), "Caderno", "UN", 1, 14.90m);
        sale.AddPayment(PaymentMethod.Pix, 4.90m);
        sale.AddPayment(PaymentMethod.Cash, 20m);

        sale.Complete(Now);

        Assert.Equal(10m, sale.ChangeAmount);
        Assert.Equal(10m, sale.NetCashReceived);
    }

    [Fact]
    public void Completed_sale_is_immutable_and_cancel_requires_reason()
    {
        var sale = NewSale();
        sale.AddItem(Guid.NewGuid(), "Caneta", "UN", 1, 2.50m);
        sale.AddPayment(PaymentMethod.Cash, 2.50m);
        sale.Complete(Now);

        Assert.Throws<DomainException>(() => sale.AddItem(Guid.NewGuid(), "X", "UN", 1, 1m));
        Assert.Throws<DomainException>(() => sale.Cancel(" ", Guid.NewGuid(), Now));

        sale.Cancel("Cliente desistiu", Guid.NewGuid(), Now);
        Assert.Equal(SaleStatus.Cancelled, sale.Status);
        Assert.Equal(FiscalStatus.NotIssued, sale.FiscalStatus);
    }

    [Fact]
    public void Removing_item_recalculates_totals()
    {
        var sale = NewSale();
        sale.AddItem(Guid.NewGuid(), "A", "UN", 1, 5m);
        sale.AddItem(Guid.NewGuid(), "B", "UN", 1, 7m);

        sale.RemoveItem(1);

        Assert.Equal(7m, sale.Total);
    }
}
