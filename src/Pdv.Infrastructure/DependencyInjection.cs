using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton(new BackupService(connectionString));
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // Retentativa em falhas transitórias é segura para vendas: a chave de idempotência evita duplicidade.
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3));
}

/// <summary>Usado apenas pelo `dotnet ef` para gerar migrações.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PdvDbContext>
{
    public PdvDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PdvDbContext>();
        DependencyInjection.Configure(builder, @"Server=(localdb)\MSSQLLocalDB;Database=PdvDesign;Trusted_Connection=True");
        return new PdvDbContext(builder.Options);
    }
}
