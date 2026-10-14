using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Identity;
using Pdv.Application.Sales;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Sales;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

[Collection(nameof(DatabaseGroup))]
public class FiscalServicesTests(DatabaseFixture fx)
{
    private static CompanySettingsModel ValidCompany(TaxRegime regime = TaxRegime.SimplesNacional, Guid? defaultProfile = null) =>
        new("Loja Exemplo Ltda", "Loja Exemplo", "12.ABC.345/01DE-35", "123456789", null, regime,
            "Rua das Flores", "100", "Sala 2", "Centro", "São Paulo", "3550308", "SP", "01001-000", "1133334444",
            defaultProfile, "Loja Centro");

    [Fact]
    public async Task Seeds_templates_default_profile_and_admin_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.NewScope();
        var profiles = await scope.ServiceProvider.GetRequiredService<TaxProfileService>().ListAsync(ct: ct);

        Assert.Contains(profiles, p => p.Csosn == "102" && p.IsDefault);
        Assert.Contains(profiles, p => p.IcmsCst == "00" && !p.IsDefault);

        var login = await scope.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync("admin", "senha123", ct);
        Assert.True(login.Value!.Can(Permissions.ManageCompany));
    }

    [Fact]
    public async Task Company_settings_require_permission_and_valid_data()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.NewScope();
        var settings = scope.ServiceProvider.GetRequiredService<CompanySettingsService>();

        Assert.False((await settings.SaveAsync(fx.OperatorUserId, ValidCompany(), ct)).Succeeded);
        Assert.False((await settings.SaveAsync(fx.AdminUserId, ValidCompany() with { Cnpj = "12.ABC.345/01DE-36" }, ct)).Succeeded);
        Assert.False((await settings.SaveAsync(fx.AdminUserId, ValidCompany() with { CityIbgeCode = "123" }, ct)).Succeeded);

        // Regime normal com o padrão do Simples (CSOSN) é recusado.
        Assert.False((await settings.SaveAsync(fx.AdminUserId, ValidCompany(TaxRegime.RegimeNormal), ct)).Succeeded);

        var saved = await settings.SaveAsync(fx.AdminUserId, ValidCompany(), ct);
        Assert.True(saved.Succeeded, saved.Error);

        var model = await settings.GetAsync(ct);
        Assert.Equal("12ABC34501DE35", model.Cnpj);
        Assert.Equal("Loja Centro", model.StoreName);
        Assert.Equal("01001000", model.ZipCode);
        Assert.True((await settings.GetReadinessAsync(ct)).CompanyConfigured);
    }

    [Fact]
    public async Task Tax_profile_service_validates_regime_and_protects_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.NewScope();
        var service = scope.ServiceProvider.GetRequiredService<TaxProfileService>();

        var normal = new TaxProfileModel(null, $"Normal {Guid.NewGuid():N}", "5102", null, "00", 18, "01", 1.65m, "01", 7.6m);
        Assert.False((await service.SaveAsync(fx.AdminUserId, normal, ct)).Succeeded); // empresa é do Simples

        var st = new TaxProfileModel(null, $"Substituição {Guid.NewGuid():N}", "5405", "500", null, 0, "49", 0, "49", 0);
        Assert.False((await service.SaveAsync(fx.OperatorUserId, st, ct)).Succeeded);
        Assert.True((await service.SaveAsync(fx.AdminUserId, st, ct)).Succeeded);

        var defaultProfile = (await service.ListAsync(ct: ct)).Single(p => p.IsDefault);
        Assert.False((await service.SetActiveAsync(fx.AdminUserId, defaultProfile.Id!.Value, false, ct)).Succeeded);
    }

    [Fact]
    public async Task Sale_stores_fiscal_snapshot_and_customer_document()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.NewScope();
        var products = scope.ServiceProvider.GetRequiredService<ProductAdminService>();
        var profiles = scope.ServiceProvider.GetRequiredService<TaxProfileService>();

        // Produto com perfil próprio (substituição tributária) e NCM/CEST.
        var stName = $"ST {Guid.NewGuid():N}";
        var stId = (await profiles.SaveAsync(fx.AdminUserId, new TaxProfileModel(null, stName, "5405", "500", null, 0, "49", 0, "49", 0), ct)).Value;
        var withProfile = await products.SaveAsync(fx.AdminUserId, new ProductEditModel(null, "Pilha AA", "UN", null, 9.99m, 5m,
            [$"78{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}"], "8506.10.19", "1000100", ProductOrigin.Nacional, stId), ct);
        Assert.True(withProfile.Succeeded, withProfile.Error);

        Assert.False((await products.SaveAsync(fx.AdminUserId, new ProductEditModel(null, "Ruim", "UN", null, 1, 1, [], "123"), ct)).Succeeded);

        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();
        var result = await checkout.FinalizeAsync(new FinalizeSaleCommand(Guid.NewGuid(), fx.Terminal.Id, fx.OperatorUserId,
            [new CheckoutItem(fx.ProductId, 1), new CheckoutItem(withProfile.Value, 2)],
            [new CheckoutPayment(PaymentMethod.Pix, 14.90m + 19.98m)],
            CustomerDocument: "529.982.247-25"), ct);
        Assert.True(result.Succeeded, result.Error);

        var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
        var sale = await db.Sales.AsNoTracking().Include(s => s.Items).SingleAsync(s => s.Id == result.Value!.SaleId, ct);
        Assert.Equal("52998224725", sale.CustomerDocument);

        var defaultItem = sale.Items.Single(i => i.ProductId == fx.ProductId);   // sem perfil próprio → padrão da empresa
        Assert.Equal("5102", defaultItem.Cfop);
        Assert.Equal("102", defaultItem.Csosn);
        Assert.Null(defaultItem.Ncm);                                           // produto do fixture ainda sem NCM
        Assert.False(defaultItem.HasFiscalData);

        var stItem = sale.Items.Single(i => i.ProductId == withProfile.Value);
        Assert.Equal("5405", stItem.Cfop);
        Assert.Equal("500", stItem.Csosn);
        Assert.Equal("85061019", stItem.Ncm);
        Assert.Equal("1000100", stItem.Cest);
        Assert.True(stItem.HasFiscalData);

        var receipt = await scope.ServiceProvider.GetRequiredService<SalesQueryService>().GetReceiptAsync(sale.Id, ct);
        Assert.Equal("52998224725", receipt!.CustomerDocument);

        var invalid = await checkout.FinalizeAsync(new FinalizeSaleCommand(Guid.NewGuid(), fx.Terminal.Id, fx.OperatorUserId,
            [new CheckoutItem(fx.ProductId, 1)], [new CheckoutPayment(PaymentMethod.Pix, 14.90m)], CustomerDocument: "111.111.111-11"), ct);
        Assert.False(invalid.Succeeded);
    }
}
