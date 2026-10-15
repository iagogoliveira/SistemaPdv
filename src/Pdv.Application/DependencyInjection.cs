using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pdv.Application.Licensing;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Purchases;
using Pdv.Application.Reports;
using Pdv.Application.Identity;
using Pdv.Application.Payments;
using Pdv.Application.Sales;
using Pdv.Application.Terminals;

namespace Pdv.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PermissionChecker>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<ProductAdminService>();
        services.AddScoped<SalesQueryService>();
        services.AddScoped<CompanySettingsService>();
        services.AddScoped<TaxProfileService>();
        services.AddScoped<NfceService>();
        services.AddScoped<NfceConfigurationService>();
        services.AddScoped<TerminalRegistry>();
        services.AddScoped<TerminalAdminService>();
        services.AddScoped<PixService>();
        services.AddScoped<PixConfigurationService>();
        // O servidor substitui por LicenseService; sem ele (testes de serviços), nada é bloqueado.
        services.TryAddScoped<ILicenseGuard, UnrestrictedLicenseGuard>();
        services.AddScoped<SupplierService>();
        services.AddScoped<PurchaseService>();
        services.AddScoped<ReturnService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ProductLookupService>();
        services.AddScoped<CashService>();
        services.AddScoped<CheckoutService>();
        return services;
    }
}
