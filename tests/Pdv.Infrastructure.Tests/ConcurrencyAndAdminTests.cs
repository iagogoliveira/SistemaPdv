using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Identity;
using Pdv.Application.Sales;
using Pdv.Domain.Cash;
using Pdv.Domain.Identity;
using Pdv.Domain.Sales;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

[Collection(nameof(DatabaseGroup))]
public class CashCloseConcurrencyTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Sale_and_close_racing_on_same_session_cannot_both_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        var terminalId = await NewTerminalWithOpenCashAsync(ct);

        // Contexto A: venda em andamento já carregou a sessão aberta.
        await using var saleScope = fx.NewScope();
        var saleDb = saleScope.ServiceProvider.GetRequiredService<PdvDbContext>();
        var session = await saleDb.CashSessions.SingleAsync(s => s.TerminalId == terminalId && s.Status == CashSessionStatus.Open, ct);
        var product = await saleDb.Products.AsNoTracking().SingleAsync(p => p.Id == fx.ProductId, ct);
        var sale = new Sale(Guid.NewGuid(), terminalId, session.Id, fx.OperatorUserId, DateTimeOffset.UtcNow);
        sale.AddItem(product.Id, product.Name, product.Unit, 1, product.SalePrice);
        sale.AddPayment(PaymentMethod.Cash, product.SalePrice);
        sale.Complete(DateTimeOffset.UtcNow);
        saleDb.Sales.Add(sale);
        session.RegisterSale(sale, fx.OperatorUserId, DateTimeOffset.UtcNow);

        // Contexto B: o caixa é fechado antes de a venda ser gravada.
        await using (var closeScope = fx.NewScope())
        {
            var closed = await closeScope.ServiceProvider.GetRequiredService<CashService>().CloseAsync(terminalId, fx.OperatorUserId, 0m, ct);
            Assert.True(closed.Succeeded, closed.Error);
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => saleDb.SaveChangesAsync(ct));

        await using var verify = fx.NewScope();
        Assert.False(await verify.ServiceProvider.GetRequiredService<PdvDbContext>().Sales.AnyAsync(s => s.Id == sale.Id, ct));
    }

    [Fact]
    public async Task Sale_after_close_is_rejected_with_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var terminalId = await NewTerminalWithOpenCashAsync(ct);
        await using var scope = fx.NewScope();
        await scope.ServiceProvider.GetRequiredService<CashService>().CloseAsync(terminalId, fx.OperatorUserId, 0m, ct);

        var result = await scope.ServiceProvider.GetRequiredService<CheckoutService>().FinalizeAsync(new FinalizeSaleCommand(
            Guid.NewGuid(), terminalId, fx.OperatorUserId, [new CheckoutItem(fx.ProductId, 1)], [new CheckoutPayment(PaymentMethod.Pix, 14.90m)]), ct);

        Assert.False(result.Succeeded);
    }

    private async Task<Guid> NewTerminalWithOpenCashAsync(CancellationToken ct)
    {
        await using var scope = fx.NewScope();
        var terminal = await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureTerminalAsync($"PC-{Guid.NewGuid():N}"[..20], ct);
        var opened = await scope.ServiceProvider.GetRequiredService<CashService>().OpenAsync(terminal.Id, fx.OperatorUserId, 0m, ct);
        Assert.True(opened.Succeeded, opened.Error);
        return terminal.Id;
    }
}

[Collection(nameof(DatabaseGroup))]
public class AdminServicesTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Product_crud_and_stock_adjustment_are_traceable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.NewScope();
        var admin = scope.ServiceProvider.GetRequiredService<ProductAdminService>();

        var denied = await admin.SaveAsync(fx.OperatorUserId, new ProductEditModel(null, "X", "UN", null, 1, 1, []), ct);
        Assert.False(denied.Succeeded);

        var created = await admin.SaveAsync(fx.AdminUserId,
            new ProductEditModel(null, "Grampeador", "un", "Papelaria", 25m, 12m, ["7899999000011", "7899999000028"]), ct);
        Assert.True(created.Succeeded, created.Error);
        var id = created.Value;

        var duplicate = await admin.SaveAsync(fx.AdminUserId, new ProductEditModel(null, "Outro", "UN", null, 1, 1, ["7899999000011"]), ct);
        Assert.False(duplicate.Succeeded);

        var updated = await admin.SaveAsync(fx.AdminUserId,
            new ProductEditModel(id, "Grampeador 26/6", "UN", "Papelaria", 27.50m, 12m, ["7899999000028", "7899999000035"]), ct);
        Assert.True(updated.Succeeded, updated.Error);

        Assert.True((await admin.RegisterEntryAsync(fx.AdminUserId, id, 10, 11.50m, "NF 123", ct)).Succeeded);
        Assert.False((await admin.AdjustToCountAsync(fx.AdminUserId, id, 8, " ", ct)).Succeeded);
        Assert.True((await admin.AdjustToCountAsync(fx.AdminUserId, id, 8, "Inventário", ct)).Succeeded);

        var item = Assert.Single(await admin.ListAsync("Grampeador", includeInactive: false, ct: ct));
        Assert.Equal(8m, item.Stock);
        Assert.Equal(11.50m, item.CostPrice);
        Assert.Equal("Papelaria", item.CategoryName);
        Assert.Contains("7899999000035", item.Barcodes, StringComparison.Ordinal);
        Assert.DoesNotContain("7899999000011", item.Barcodes, StringComparison.Ordinal);

        var movements = await admin.ListMovementsAsync(id, ct: ct);
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, m => m.Quantity == -2 && m.Reason == "Inventário");
    }

    [Fact]
    public async Task User_admin_requires_permission_and_new_user_can_login()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.NewScope();
        var users = scope.ServiceProvider.GetRequiredService<UserAdminService>();
        var auth = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
        var supervisorRole = (await users.ListRolesAsync(ct)).Single(r => r.Name == "Supervisor").Id;

        Assert.False((await users.CreateAsync(fx.OperatorUserId, "x", "X", "senha123", supervisorRole, ct)).Succeeded);

        var created = await users.CreateAsync(fx.AdminUserId, "super1", "Supervisora", "senha123", supervisorRole, ct);
        Assert.True(created.Succeeded, created.Error);
        Assert.False((await users.CreateAsync(fx.AdminUserId, "SUPER1", "Dup", "senha123", supervisorRole, ct)).Succeeded);

        // Supervisor libera desconto; operador não; supervisor não gerencia usuários.
        Assert.True((await auth.AuthorizeAsync("super1", "senha123", Permissions.ApplyDiscount, ct)).Succeeded);
        Assert.False((await auth.AuthorizeAsync("super1", "errada", Permissions.ApplyDiscount, ct)).Succeeded);
        Assert.False((await auth.AuthorizeAsync("caixa1", "senha123", Permissions.ApplyDiscount, ct)).Succeeded);
        Assert.False((await auth.AuthorizeAsync("super1", "senha123", Permissions.ManageUsers, ct)).Succeeded);

        Assert.True((await users.ResetPasswordAsync(fx.AdminUserId, created.Value, "nova1234", ct)).Succeeded);
        Assert.True((await auth.LoginAsync("super1", "nova1234", ct)).Succeeded);

        Assert.True((await users.SetActiveAsync(fx.AdminUserId, created.Value, false, ct)).Succeeded);
        Assert.False((await auth.LoginAsync("super1", "nova1234", ct)).Succeeded);
        Assert.False((await users.SetActiveAsync(fx.AdminUserId, fx.AdminUserId, false, ct)).Succeeded);
    }

    [Fact]
    public async Task Receipt_contains_sale_snapshot_and_split_payments()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.NewScope();
        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();

        var result = await checkout.FinalizeAsync(new FinalizeSaleCommand(Guid.NewGuid(), fx.Terminal.Id, fx.OperatorUserId,
            [new CheckoutItem(fx.ProductId, 2, Discount: 0.80m)],
            [new CheckoutPayment(PaymentMethod.Pix, 9m), new CheckoutPayment(PaymentMethod.Cash, 30m)],
            DiscountAuthorizedByUserId: fx.AdminUserId), ct);
        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Value!.Number > 0);

        var queries = scope.ServiceProvider.GetRequiredService<SalesQueryService>();
        var receipt = await queries.GetReceiptAsync(result.Value.SaleId, ct);
        Assert.NotNull(receipt);
        Assert.Equal(29.80m, receipt.Subtotal);
        Assert.Equal(0.80m, receipt.DiscountTotal);
        Assert.Equal(29.00m, receipt.Total);
        Assert.Equal(10.00m, receipt.Change);
        Assert.Equal(2, receipt.Payments.Count);
        Assert.Equal("001", receipt.TerminalCode);

        Assert.Contains(await queries.ListRecentAsync(fx.Terminal.Id, ct: ct), s => s.Id == result.Value.SaleId);
    }
}
