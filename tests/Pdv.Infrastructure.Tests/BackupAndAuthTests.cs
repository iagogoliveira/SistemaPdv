using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Identity;
using Pdv.Domain.Catalog;
using Pdv.Infrastructure.Persistence;
using Pdv.Infrastructure.Security;

namespace Pdv.Infrastructure.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_is_salted_and_verifiable()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var a = hasher.Hash("segredo");
        var b = hasher.Hash("segredo");

        Assert.NotEqual(a, b);
        Assert.True(hasher.Verify("segredo", a));
        Assert.False(hasher.Verify("Segredo", a));
    }
}

[Collection(nameof(DatabaseGroup))]
public class AuthenticationTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Login_validates_password_and_loads_permissions()
    {
        await using var scope = fx.NewScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
        var ct = TestContext.Current.CancellationToken;

        Assert.False((await auth.LoginAsync("admin", "errada", ct)).Succeeded);

        var ok = await auth.LoginAsync(" ADMIN ", "senha123", ct);
        Assert.True(ok.Succeeded);
        Assert.True(ok.Value!.Can(Domain.Identity.Permissions.ManageUsers));
        Assert.False((await auth.CreateInitialAdminAsync("outro", "Outro", "senha123", ct)).Succeeded);
    }
}

/// <summary>Critério de aceite V0.1: backup restaurável. Usa banco próprio para poder sobrescrevê-lo.</summary>
public class BackupTests
{
    [Fact]
    public async Task Backup_then_restore_recovers_previous_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var fx = new DatabaseFixture();
        await fx.InitializeAsync();
        var backupFile = Path.Combine(Path.GetTempPath(), $"pdv-test-{Guid.NewGuid():N}.bak");
        try
        {
            var backup = fx.Services.GetRequiredService<BackupService>();
            await backup.BackupAsync(backupFile, ct);

            await using (var scope = fx.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
                db.Products.Add(new Product("Criado após o backup", 1m, 1m));
                await db.SaveChangesAsync(ct);
            }

            await backup.RestoreAsync(backupFile, ct);

            await using (var scope = fx.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
                Assert.False(await db.Products.AnyAsync(p => p.Name == "Criado após o backup", ct));
                Assert.True(await db.Products.AnyAsync(p => p.Name == "Caderno", ct));
            }
        }
        finally
        {
            await fx.DisposeAsync();
            File.Delete(backupFile);
        }
    }
}
