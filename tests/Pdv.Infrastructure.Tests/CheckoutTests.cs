using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Cash;
using Pdv.Application.Sales;
using Pdv.Domain.Sales;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

[Collection(nameof(DatabaseGroup))]
public class CheckoutTests(DatabaseFixture fx)
{
    private FinalizeSaleCommand Command(Guid requestId, decimal quantity = 1, decimal discount = 0, decimal cash = 50m) =>
        new(requestId, fx.Terminal.Id, fx.OperatorUserId,
            [new CheckoutItem(fx.ProductId, quantity, discount)],
            [new CheckoutPayment(PaymentMethod.Cash, cash)]);

    private async Task<Result> FinalizeInNewScopeAsync(FinalizeSaleCommand command)
    {
        await using var scope = fx.NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<CheckoutService>().FinalizeAsync(command, TestContext.Current.CancellationToken);
        return new Result(result.Succeeded, result.Value, result.Error);
    }

    private sealed record Result(bool Succeeded, SaleReceipt? Receipt, string? Error);

    private async Task<(int Sales, int StockMovements, int CashMovements)> CountAsync(Guid requestId)
    {
        await using var scope = fx.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var saleIds = await db.Sales.Where(s => s.ClientRequestId == requestId).Select(s => s.Id).ToListAsync(ct);
        return (saleIds.Count,
            await db.StockMovements.CountAsync(m => m.ReferenceId != null && saleIds.Contains(m.ReferenceId.Value), ct),
            await db.CashMovements.CountAsync(m => m.ReferenceId != null && saleIds.Contains(m.ReferenceId.Value), ct));
    }

    [Fact]
    public async Task Finalize_persists_sale_stock_and_cash_atomically()
    {
        var requestId = Guid.NewGuid();

        var result = await FinalizeInNewScopeAsync(Command(requestId, quantity: 2, cash: 50m));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(29.80m, result.Receipt!.Total);
        Assert.Equal(20.20m, result.Receipt.Change);
        Assert.Equal((1, 1, 1), await CountAsync(requestId));
    }

    [Fact]
    public async Task Repeated_request_does_not_create_second_sale()
    {
        var requestId = Guid.NewGuid();

        var first = await FinalizeInNewScopeAsync(Command(requestId));
        var second = await FinalizeInNewScopeAsync(Command(requestId));

        Assert.False(first.Receipt!.AlreadyProcessed);
        Assert.True(second.Receipt!.AlreadyProcessed);
        Assert.Equal(first.Receipt.SaleId, second.Receipt.SaleId);
        Assert.Equal((1, 1, 1), await CountAsync(requestId));
    }

    [Fact]
    public async Task Concurrent_duplicate_requests_create_exactly_one_sale()
    {
        var requestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => FinalizeInNewScopeAsync(Command(requestId))));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        Assert.Single(results.Select(r => r.Receipt!.SaleId).Distinct());
        Assert.Equal((1, 1, 1), await CountAsync(requestId));
    }

    [Fact]
    public async Task Failed_sale_leaves_nothing_behind()
    {
        var requestId = Guid.NewGuid();

        var result = await FinalizeInNewScopeAsync(Command(requestId, quantity: 10, cash: 1m));

        Assert.False(result.Succeeded);
        Assert.Equal((0, 0, 0), await CountAsync(requestId));
    }

    [Fact]
    public async Task Operator_without_permission_cannot_give_discount()
    {
        var result = await FinalizeInNewScopeAsync(Command(Guid.NewGuid(), discount: 1m));
        Assert.False(result.Succeeded);

        var authorized = await FinalizeInNewScopeAsync(Command(Guid.NewGuid(), discount: 1m) with { DiscountAuthorizedByUserId = fx.AdminUserId });
        Assert.True(authorized.Succeeded, authorized.Error);
        Assert.Equal(13.90m, authorized.Receipt!.Total);
    }

    [Fact]
    public async Task Cancel_creates_compensating_movements()
    {
        var requestId = Guid.NewGuid();
        var sale = await FinalizeInNewScopeAsync(Command(requestId, quantity: 3, cash: 44.70m));

        await using var scope = fx.NewScope();
        var ct = TestContext.Current.CancellationToken;
        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();

        var denied = await checkout.CancelAsync(sale.Receipt!.SaleId, "Erro", fx.OperatorUserId, ct);
        Assert.False(denied.Succeeded);

        var cancelled = await checkout.CancelAsync(sale.Receipt.SaleId, "Cliente desistiu", fx.AdminUserId, ct);
        Assert.True(cancelled.Succeeded, cancelled.Error);

        var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
        var stockNet = await db.StockMovements.Where(m => m.ReferenceId == sale.Receipt.SaleId).SumAsync(m => m.Quantity, ct);
        var movements = await db.StockMovements.CountAsync(m => m.ReferenceId == sale.Receipt.SaleId, ct);
        Assert.Equal(0m, stockNet);
        Assert.Equal(2, movements);
    }
}

[Collection(nameof(DatabaseGroup))]
public class CashServiceTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Only_one_open_session_per_terminal()
    {
        await using var scope = fx.NewScope();
        var cash = scope.ServiceProvider.GetRequiredService<CashService>();

        var result = await cash.OpenAsync(fx.Terminal.Id, fx.OperatorUserId, 10m, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Operator_without_permission_cannot_withdraw()
    {
        await using var scope = fx.NewScope();
        var cash = scope.ServiceProvider.GetRequiredService<CashService>();

        var result = await cash.WithdrawAsync(fx.Terminal.Id, fx.OperatorUserId, 10m, "Depósito", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }
}
