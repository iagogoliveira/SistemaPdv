using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Sales;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

/// <summary>SEFAZ simulada: cada chamada consome a próxima resposta programada (ou autoriza, por padrão).</summary>
public sealed class FakeSefaz : INfceGateway
{
    private readonly ConcurrentQueue<SefazResult> _authorize = new();
    private readonly ConcurrentQueue<SefazResult> _query = new();
    private readonly ConcurrentDictionary<string, string> _authorizedKeys = new();
    private int _protocol = 135_000_000;

    private int _authorizeCalls;
    private int _queryCalls;

    public int AuthorizeCalls => Volatile.Read(ref _authorizeCalls);
    public int QueryCalls => Volatile.Read(ref _queryCalls);
    public List<NfceDraft> Drafts { get; } = [];

    public void NextAuthorize(params SefazResult[] results) { foreach (var r in results) _authorize.Enqueue(r); }
    public void NextQuery(params SefazResult[] results) { foreach (var r in results) _query.Enqueue(r); }

    public SignedNfce BuildAndSign(NfceDraft draft, NfceSettings settings)
    {
        lock (Drafts) Drafts.Add(draft);
        return new SignedNfce($"<NFe><infNFe Id=\"NFe{draft.AccessKey}\" /></NFe>", $"https://qr.test/?p={draft.AccessKey}|3|2", "https://consulta.test");
    }

    public Task<SefazResult> AuthorizeAsync(string signedXml, NfceSettings settings, string uf, CancellationToken ct)
    {
        Interlocked.Increment(ref _authorizeCalls);
        var key = System.Text.RegularExpressions.Regex.Match(signedXml, "Id=\"NFe([0-9A-Z]{44})\"").Groups[1].Value;
        var result = _authorize.TryDequeue(out var r) ? r : Authorized();
        if (result.Outcome == SefazOutcome.Authorized) _authorizedKeys[key] = result.Protocol!;
        return Task.FromResult(result);
    }

    public Task<SefazResult> QueryAsync(string accessKey, NfceSettings settings, string uf, CancellationToken ct)
    {
        Interlocked.Increment(ref _queryCalls);
        if (_query.TryDequeue(out var r)) return Task.FromResult(r);
        return Task.FromResult(_authorizedKeys.TryGetValue(accessKey, out var protocol)
            ? new SefazResult(SefazOutcome.Authorized, 100, "Autorizado o uso da NF-e", protocol)
            : new SefazResult(SefazOutcome.NotFound, 217, "NF-e não consta na base de dados da SEFAZ"));
    }

    public Task<SefazResult> CancelAsync(string accessKey, string protocol, string cnpj, string justification, NfceSettings settings,
        string uf, CancellationToken ct) =>
        Task.FromResult(new SefazResult(SefazOutcome.Authorized, 135, "Evento registrado e vinculado a NF-e", "135999"));

    public Task<SefazResult> ServiceStatusAsync(NfceSettings settings, string uf, CancellationToken ct) =>
        Task.FromResult(new SefazResult(SefazOutcome.Authorized, 107, "Serviço em Operação"));

    public SefazResult Authorized() =>
        new(SefazOutcome.Authorized, 100, "Autorizado o uso da NF-e", Interlocked.Increment(ref _protocol).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "<nfeProc/>", DateTimeOffset.UtcNow);
}

public sealed class InMemoryNfceSettings : INfceSettingsStore
{
    public NfceSettings Current { get; set; } =
        new(true, FiscalEnvironment.Homologation, 1, "teste.pfx", "senha", "000001", "CSCTESTE", CancellationWindowMinutes: 30);

    public NfceSettings Load() => Current;
    public void Save(NfceSettings settings) => Current = settings;
}

/// <summary>Banco próprio, empresa configurada e produtos com NCM, para os testes de emissão.</summary>
public sealed class NfceFixture : DatabaseFixture
{
    public FakeSefaz Sefaz { get; } = new();
    public InMemoryNfceSettings Settings { get; } = new();
    public Guid FiscalProductId { get; private set; }
    public Guid ProductWithoutNcmId { get; private set; }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INfceGateway>(Sefaz);
        services.AddSingleton<INfceSettingsStore>(Settings);
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await using var scope = NewScope();
        var company = await scope.ServiceProvider.GetRequiredService<CompanySettingsService>().SaveAsync(AdminUserId,
            new CompanySettingsModel("Papelaria Teste Ltda", "Papelaria Teste", "11.222.333/0001-81", "123456789", null,
                TaxRegime.SimplesNacional, "Praça da Sé", "100", null, "Sé", "São Paulo", "3550308", "SP", "01001000", null, null, "Loja"));
        if (!company.Succeeded) throw new InvalidOperationException(company.Error);

        var products = scope.ServiceProvider.GetRequiredService<ProductAdminService>();
        FiscalProductId = (await products.SaveAsync(AdminUserId, new ProductEditModel(null, "Caderno fiscal", "UN", null, 10m, 5m,
            ["7891000315507"], "48202000"))).Value;
        ProductWithoutNcmId = ProductId; // produto base do fixture, sem NCM
    }

    public async Task<Guid> SellAsync(Guid productId, decimal price = 10m, string? customer = null)
    {
        await using var scope = NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<CheckoutService>().FinalizeAsync(new FinalizeSaleCommand(
            Guid.NewGuid(), Terminal.Id, OperatorUserId, [new CheckoutItem(productId, 1)], [new CheckoutPayment(PaymentMethod.Pix, price)],
            CustomerDocument: customer));
        if (!result.Succeeded) throw new InvalidOperationException(result.Error);
        return result.Value!.SaleId;
    }

    public async Task<Pdv.Application.Result<NfceInfo>> IssueAsync(Guid saleId)
    {
        await using var scope = NewScope();
        return await scope.ServiceProvider.GetRequiredService<NfceService>().IssueForSaleAsync(saleId);
    }
}

[CollectionDefinition(nameof(NfceGroup))]
public sealed class NfceGroup : ICollectionFixture<NfceFixture>;

[Collection(nameof(NfceGroup))]
public class NfceServiceTests(NfceFixture fx)
{
    [Fact]
    public async Task Authorizes_with_sequential_number_and_valid_key()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId, customer: "529.982.247-25");

        var result = await fx.IssueAsync(saleId);

        Assert.True(result.Succeeded, result.Error);
        var info = result.Value!;
        Assert.Equal(FiscalDocumentStatus.Authorized, info.Status);
        Assert.True(AccessKey.IsValid(info.AccessKey));
        Assert.Equal(FiscalEmissionType.Normal, info.EmissionType);
        Assert.NotNull(info.Protocol);
        Assert.Contains(fx.Sefaz.Drafts, d => d.AccessKey == info.AccessKey && d.CustomerDocument == "52998224725");
    }

    [Fact]
    public async Task Issuing_twice_does_not_send_again()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        var first = await fx.IssueAsync(saleId);
        var calls = fx.Sefaz.AuthorizeCalls;

        var second = await fx.IssueAsync(saleId);

        Assert.Equal(first.Value!.DocumentId, second.Value!.DocumentId);
        Assert.Equal(first.Value.Number, second.Value.Number);
        Assert.Equal(calls, fx.Sefaz.AuthorizeCalls);
    }

    [Fact]
    public async Task Sefaz_down_issues_offline_contingency_and_transmits_later()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        fx.Sefaz.NextAuthorize(new SefazResult(SefazOutcome.Unavailable, 0, "Sem conexão"));

        var offline = await fx.IssueAsync(saleId);

        Assert.True(offline.Succeeded, offline.Error);
        Assert.Equal(FiscalDocumentStatus.Contingency, offline.Value!.Status);
        Assert.Equal(FiscalEmissionType.OfflineContingency, offline.Value.EmissionType);
        Assert.Equal('9', offline.Value.AccessKey[34]);

        // SEFAZ volta: consulta (não encontra) e transmite o mesmo XML de contingência.
        await using (var scope = fx.NewScope())
            await scope.ServiceProvider.GetRequiredService<NfceService>().ProcessPendingAsync(ct: TestContext.Current.CancellationToken);

        await using var check = fx.NewScope();
        var info = await check.ServiceProvider.GetRequiredService<NfceService>().GetBySaleAsync(saleId, TestContext.Current.CancellationToken);
        Assert.Equal(FiscalDocumentStatus.Authorized, info!.Status);
        Assert.Equal(offline.Value.AccessKey, info.AccessKey);
        Assert.Equal(offline.Value.Number, info.Number);
    }

    [Fact]
    public async Task Timeout_then_query_finds_authorization_without_resending()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        fx.Sefaz.NextAuthorize(new SefazResult(SefazOutcome.Unknown, 0, "Tempo esgotado"));
        var first = await fx.IssueAsync(saleId);
        Assert.Equal(FiscalDocumentStatus.Unknown, first.Value!.Status);

        // A SEFAZ tinha processado: a consulta encontra a autorização.
        fx.Sefaz.NextQuery(new SefazResult(SefazOutcome.Authorized, 100, "Autorizado o uso da NF-e", "135777"));
        var calls = fx.Sefaz.AuthorizeCalls;
        var second = await fx.IssueAsync(saleId);

        Assert.Equal(FiscalDocumentStatus.Authorized, second.Value!.Status);
        Assert.Equal("135777", second.Value.Protocol);
        Assert.Equal(calls, fx.Sefaz.AuthorizeCalls);
        Assert.Equal(first.Value.AccessKey, second.Value.AccessKey);
    }

    [Fact]
    public async Task Duplicate_rejection_becomes_query()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        fx.Sefaz.NextAuthorize(new SefazResult(SefazOutcome.Rejected, 204, "Duplicidade de NF-e"));
        fx.Sefaz.NextQuery(new SefazResult(SefazOutcome.Authorized, 100, "Autorizado o uso da NF-e", "135888"));

        var result = await fx.IssueAsync(saleId);

        Assert.Equal(FiscalDocumentStatus.Authorized, result.Value!.Status);
        Assert.Equal("135888", result.Value.Protocol);
    }

    [Fact]
    public async Task Rejection_keeps_number_and_retry_resigns()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        fx.Sefaz.NextAuthorize(new SefazResult(SefazOutcome.Rejected, 778, "Rejeição: Informado NCM inexistente"));
        var rejected = await fx.IssueAsync(saleId);
        Assert.Equal(FiscalDocumentStatus.Rejected, rejected.Value!.Status);
        Assert.Equal(778, rejected.Value.StatusCode);

        var retried = await fx.IssueAsync(saleId);

        Assert.Equal(FiscalDocumentStatus.Authorized, retried.Value!.Status);
        Assert.Equal(rejected.Value.Number, retried.Value.Number);
        Assert.NotEqual(rejected.Value.AccessKey, retried.Value.AccessKey);   // novo cNF/data → nova chave
    }

    [Fact]
    public async Task Denied_consumes_number()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        fx.Sefaz.NextAuthorize(new SefazResult(SefazOutcome.Denied, 302, "Uso Denegado: Irregularidade fiscal do destinatário"));

        var result = await fx.IssueAsync(saleId);

        Assert.Equal(FiscalDocumentStatus.Denied, result.Value!.Status);
        var again = await fx.IssueAsync(saleId);
        Assert.Equal(FiscalDocumentStatus.Denied, again.Value!.Status);
    }

    [Fact]
    public async Task Items_without_ncm_block_emission_without_reserving_number()
    {
        var saleId = await fx.SellAsync(fx.ProductWithoutNcmId, price: 14.90m);

        var result = await fx.IssueAsync(saleId);

        Assert.False(result.Succeeded);
        Assert.Contains("NCM", result.Error, StringComparison.Ordinal);
        await using var scope = fx.NewScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PdvDbContext>().FiscalDocuments.AnyAsync(d => d.SaleId == saleId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_emissions_get_unique_numbers()
    {
        var sales = new List<Guid>();
        for (var i = 0; i < 6; i++) sales.Add(await fx.SellAsync(fx.FiscalProductId));

        var results = await Task.WhenAll(sales.Select(fx.IssueAsync));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        var numbers = results.Select(r => r.Value!.Number).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public async Task Cancellation_rules()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        await fx.IssueAsync(saleId);
        await using var scope = fx.NewScope();
        var service = scope.ServiceProvider.GetRequiredService<NfceService>();

        Assert.False((await service.CancelAsync(saleId, "Cliente desistiu da compra", fx.OperatorUserId, TestContext.Current.CancellationToken)).Succeeded);
        Assert.False((await service.CancelAsync(saleId, "curta", fx.AdminUserId, TestContext.Current.CancellationToken)).Succeeded);

        var cancelled = await service.CancelAsync(saleId, "Cliente desistiu da compra", fx.AdminUserId, TestContext.Current.CancellationToken);
        Assert.True(cancelled.Succeeded, cancelled.Error);
        Assert.Equal(FiscalDocumentStatus.Cancelled, cancelled.Value!.Status);
    }

    [Fact]
    public async Task Disabled_or_incomplete_settings_are_reported()
    {
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        var original = fx.Settings.Current;
        try
        {
            fx.Settings.Current = original with { Enabled = false };
            Assert.False((await fx.IssueAsync(saleId)).Succeeded);
            fx.Settings.Current = original with { CscToken = null };
            Assert.False((await fx.IssueAsync(saleId)).Succeeded);
        }
        finally
        {
            fx.Settings.Current = original;
        }
    }
}
