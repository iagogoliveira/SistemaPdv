using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Purchases;
using Pdv.Application.Reports;
using Pdv.Application.Identity;
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
