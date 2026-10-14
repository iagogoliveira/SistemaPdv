using Pdv.Domain.Cash;
using Pdv.Domain.Common;
using Pdv.Domain.Inventory;
using Pdv.Domain.Sales;

namespace Pdv.Domain.Tests;

public class CashSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void Close_identifies_shortage()
    {
        var session = CashSession.Open(Guid.NewGuid(), UserId, 100m, Now);
        var sale = new Sale(Guid.NewGuid(), session.TerminalId, session.Id, UserId, Now);
        sale.AddItem(Guid.NewGuid(), "Item", "UN", 1, 50m);
        sale.AddPayment(PaymentMethod.Cash, 50m);
        sale.Complete(Now);
        session.RegisterSale(sale, UserId, Now);
        session.RegisterSupply(20m, "Troco extra", UserId, Now);
        session.RegisterWithdrawal(70m, "Depósito", UserId, Now);

        session.Close(95m, UserId, Now);

        Assert.Equal(100m, session.ExpectedAmount);
        Assert.Equal(-5m, session.Difference);
        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.Equal(1, session.SaleCount);
    }

    [Fact]
    public void Every_operation_bumps_revision()
    {
        var session = CashSession.Open(Guid.NewGuid(), UserId, 10m, Now);
        var before = session.Revision;

        var sale = new Sale(Guid.NewGuid(), session.TerminalId, session.Id, UserId, Now);
        sale.AddItem(Guid.NewGuid(), "Item", "UN", 1, 5m);
        sale.AddPayment(PaymentMethod.Pix, 5m);
        sale.Complete(Now);
        session.RegisterSale(sale, UserId, Now);

        Assert.True(session.Revision > before);
        Assert.Single(session.Movements); // venda em Pix não gera movimento de dinheiro
    }

    [Fact]
    public void Withdrawal_cannot_exceed_cash_balance_and_requires_reason()
    {
        var session = CashSession.Open(Guid.NewGuid(), UserId, 50m, Now);

        Assert.Throws<DomainException>(() => session.RegisterWithdrawal(60m, "Depósito", UserId, Now));
        Assert.Throws<DomainException>(() => session.RegisterWithdrawal(10m, "", UserId, Now));
    }

    [Fact]
    public void Closed_session_rejects_movements()
    {
        var session = CashSession.Open(Guid.NewGuid(), UserId, 0m, Now);
        session.Close(0m, UserId, Now);

        Assert.Throws<DomainException>(() => session.RegisterSupply(10m, null, UserId, Now));
        Assert.Throws<DomainException>(() => session.Close(0m, UserId, Now));
    }
}

public class StockMovementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sale_movement_is_negative_and_traceable()
    {
        var saleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var movement = StockMovement.ForSale(Guid.NewGuid(), 2.5m, saleId, userId, Now);

        Assert.Equal(-2.5m, movement.Quantity);
        Assert.Equal(saleId, movement.ReferenceId);
        Assert.Equal(userId, movement.UserId);
        Assert.Equal(Now, movement.OccurredAt);
    }

    [Fact]
    public void Adjustment_requires_reason_and_non_zero_quantity()
    {
        Assert.Throws<DomainException>(() => StockMovement.ForAdjustment(Guid.NewGuid(), -1, " ", Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => StockMovement.ForAdjustment(Guid.NewGuid(), 0, "Inventário", Guid.NewGuid(), Now));
    }
}
