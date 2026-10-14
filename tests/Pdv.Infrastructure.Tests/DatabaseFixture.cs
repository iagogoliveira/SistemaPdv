using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application;
using Pdv.Application.Cash;
using Pdv.Application.Identity;
using Pdv.Domain.Catalog;
using Pdv.Domain.Inventory;
using Pdv.Domain.Stores;
using Pdv.Infrastructure;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

/// <summary>
/// Banco real no SQL Server LocalDB (instalado com o Visual Studio). Variável PDV_TEST_SQL substitui o servidor.
/// Cada execução usa um banco próprio, removido ao final.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public string ConnectionString { get; } =
        $@"Server={Environment.GetEnvironmentVariable("PDV_TEST_SQL") ?? @"(localdb)\MSSQLLocalDB"};Database=PdvTests_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    public ServiceProvider Services { get; private set; } = null!;
    public Guid AdminUserId { get; private set; }
    public Guid OperatorUserId { get; private set; }
    public Terminal Terminal { get; private set; } = null!;
    public Guid ProductId { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Services = new ServiceCollection().AddApplication().AddInfrastructure(ConnectionString).BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await init.InitializeAsync();
        Terminal = await init.EnsureTerminalAsync("TEST-PC");

        var auth = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
        AdminUserId = (await auth.CreateInitialAdminAsync("admin", "Admin", "senha123")).Value!.UserId;

        var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var operatorRole = await db.Roles.SingleAsync(r => r.Name == "Operador");
        var op = new Domain.Identity.User("caixa1", "Caixa 1", hasher.Hash("senha123"), operatorRole.Id, DateTimeOffset.UtcNow);
        db.Users.Add(op);
        OperatorUserId = op.Id;

        var product = new Product("Caderno", 14.90m, 8m);
        product.AddBarcode("7890000000001");
        db.Products.Add(product);
        db.StockMovements.Add(StockMovement.ForPurchase(product.Id, 100, null, AdminUserId, DateTimeOffset.UtcNow));
        ProductId = product.Id;
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<CashService>().OpenAsync(Terminal.Id, OperatorUserId, 100m);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PdvDbContext>().Database.EnsureDeletedAsync();
        await Services.DisposeAsync();
    }

    public AsyncServiceScope NewScope() => Services.CreateAsyncScope();
}

[CollectionDefinition(nameof(DatabaseGroup))]
public sealed class DatabaseGroup : ICollectionFixture<DatabaseFixture>;
