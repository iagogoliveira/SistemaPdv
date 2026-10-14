using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Catalog;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;
using Pdv.Domain.Stores;

namespace Pdv.Infrastructure.Persistence;

public sealed class DatabaseInitializer(PdvDbContext db, TimeProvider clock)
{
    /// <summary>Aplica migrações pendentes e garante os dados mínimos (perfis, empresa, loja).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        if (!await db.Roles.AnyAsync(ct))
        {
            db.Roles.AddRange(
                new Role(AuthenticationService.AdminRoleName, Permissions.All),
                new Role("Supervisor", [Permissions.ApplyDiscount, Permissions.CancelSale, Permissions.ManageCash, Permissions.ViewReports]),
                new Role("Operador", []));
        }

        if (!await db.Stores.AnyAsync(ct))
        {
            var company = new Company("Minha Empresa", null);
            db.Companies.Add(company);
            db.Stores.Add(new Store(company.Id, "Loja Principal"));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Identifica o caixa pelo nome do computador, registrando-o na primeira execução.</summary>
    public async Task<Terminal> EnsureTerminalAsync(string machineName, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.MachineName == machineName, ct);
        if (terminal is not null) return terminal;

        var store = await db.Stores.OrderBy(s => s.Id).FirstAsync(ct);
        var next = await db.Terminals.CountAsync(t => t.StoreId == store.Id, ct) + 1;
        terminal = new Terminal(store.Id, next.ToString("000", System.Globalization.CultureInfo.InvariantCulture), machineName);
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync(ct);
        return terminal;
    }

    /// <summary>Produtos de demonstração para desenvolvimento e apresentações.</summary>
    public async Task SeedDemoDataAsync(Guid userId, CancellationToken ct = default)
    {
        if (await db.Products.AnyAsync(ct)) return;

        var now = clock.GetUtcNow();
        var demo = new (string Name, decimal Price, decimal Cost, string Barcode)[]
        {
            ("Caderno 96 folhas", 14.90m, 8.20m, "7891000000011"),
            ("Caneta esferográfica azul", 2.50m, 0.90m, "7891000000028"),
            ("Pilha AA (par)", 9.99m, 5.10m, "7891000000035"),
            ("Copo plástico 300 ml (50 un)", 7.49m, 3.80m, "7891000000042"),
            ("Fita adesiva transparente", 4.25m, 1.70m, "7891000000059"),
        };

        foreach (var (name, price, cost, barcode) in demo)
        {
            var product = new Product(name, price, cost);
            product.AddBarcode(barcode);
            db.Products.Add(product);
            db.StockMovements.Add(StockMovement.ForPurchase(product.Id, 50, null, userId, now));
        }

        await db.SaveChangesAsync(ct);
    }
}
