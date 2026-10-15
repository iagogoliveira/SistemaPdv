using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pdv.Application.Payments;
using Pdv.Infrastructure.Payments;
using Pdv.Application;
using Pdv.Infrastructure.Persistence;
using Pdv.Infrastructure.Security;

namespace Pdv.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<PdvDbContext>(options => Configure(options, connectionString));
        services.AddScoped<IPdvDbContext>(sp => sp.GetRequiredService<PdvDbContext>());
        services.AddScoped<DatabaseInitializer>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IPgTools, LocalPgTools>();
        services.AddSingleton(sp => new BackupService(connectionString, sp.GetRequiredService<IPgTools>()));
        // Pix: provedores disponíveis; a configuração vem do servidor (FilePixSettingsStore) ou fica desligada.
        services.TryAddSingleton<IPixSettingsStore, InMemoryPixSettingsStore>();
        services.AddSingleton<IPixProvider, SimulatedPixProvider>();
        services.AddSingleton<IPixProvider>(_ => new BacenPixProvider());
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // Retentativa em falhas transitórias é segura para vendas: a chave de idempotência evita duplicidade.
        options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3));
}

/// <summary>Usado apenas pelo `dotnet ef` para gerar migrações.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PdvDbContext>
{
    public PdvDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PdvDbContext>();
        DependencyInjection.Configure(builder, "Host=localhost;Database=pdv_design;Username=pdv;Password=pdv_dev");
        return new PdvDbContext(builder.Options);
    }
}
