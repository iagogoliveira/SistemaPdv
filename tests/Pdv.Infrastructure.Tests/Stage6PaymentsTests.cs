using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application;
using Pdv.Application.Payments;
using Pdv.Application.Sales;
using Pdv.Domain.Common;
using Pdv.Domain.Payments;
using Pdv.Domain.Sales;
using Pdv.Infrastructure.Payments;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

[Collection(nameof(DatabaseGroup))]
public class Stage6PaymentsTests(DatabaseFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InMemoryPixSettingsStore Settings => (InMemoryPixSettingsStore)fx.Services.GetRequiredService<IPixSettingsStore>();

    private void EnableSimulator(int paysAfterSeconds) =>
        Settings.Current = PixSettings.Disabled with
        {
            Enabled = true, Provider = PixSettings.Simulated, PspCnpj = "99999999000191", PspName = "PSP Teste", SimulatorPaysAfterSeconds = paysAfterSeconds,
        };

    private async Task<T> RunAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        await using var scope = fx.NewScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private FinalizeSaleCommand Sale(Guid requestId, params CheckoutPayment[] payments) =>
        new(requestId, fx.Terminal.Id, fx.OperatorUserId, [new CheckoutItem(fx.ProductId, 1)], payments);

    [Fact]
    public async Task Dynamic_pix_must_be_confirmed_and_pays_a_single_sale()
    {
        EnableSimulator(paysAfterSeconds: 3600); // ainda não pago
        var cart = Guid.NewGuid();
        var charge = await RunAsync<PixService, PixChargeView>(s => s.CreateChargeAsync(fx.Terminal.Id, cart, 14.90m, Ct));
        Assert.Equal(PixChargeStatus.Active, charge.Status);
        Assert.True(BrCode.IsValid(charge.CopyPaste));
        Assert.Matches("^[A-Za-z0-9]{26,35}$", charge.Txid);

        // Mesmo carrinho e valor: reaproveita a cobrança aberta (QR reaberto não gera outra).
        var again = await RunAsync<PixService, PixChargeView>(s => s.CreateChargeAsync(fx.Terminal.Id, cart, 14.90m, Ct));
        Assert.Equal(charge.Id, again.Id);

        // Venda com Pix ainda não pago é recusada.
        var pix = new CheckoutPayment(PaymentMethod.Pix, 14.90m, PixChargeId: charge.Id);
        var refused = await RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(Sale(cart, pix), Ct));
        Assert.Contains("não foi confirmado", refused.Error);

        // O PSP confirma: a venda é gravada com o Pix integrado (EndToEndId e CNPJ do PSP).
        EnableSimulator(paysAfterSeconds: 0);
        var paid = await RunAsync<PixService, PixChargeView>(s => s.CheckAsync(fx.Terminal.Id, charge.Id, Ct));
        Assert.Equal(PixChargeStatus.Paid, paid.Status);
        Assert.StartsWith("E", paid.EndToEndId);
        var sale = await RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(Sale(cart, pix), Ct));
        Assert.True(sale.Succeeded, sale.Error);

        await using (var scope = fx.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
            var payment = await db.Sales.Where(s => s.Id == sale.Value!.SaleId).SelectMany(s => s.Payments).SingleAsync(Ct);
            Assert.True(payment.IsIntegrated);
            Assert.Equal(paid.EndToEndId, payment.TransactionId);
            Assert.Equal("99999999000191", payment.AcquirerCnpj);
            Assert.Equal(sale.Value!.SaleId, (await db.PixCharges.SingleAsync(c => c.Id == charge.Id, Ct)).SaleId);
        }

        // O mesmo Pix não quita outra venda; valor diferente também é recusado.
        var other = await RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(Sale(Guid.NewGuid(), pix), Ct));
        Assert.Contains("outra venda", other.Error);

        // Venda ativa: o Pix não pode ser devolvido.
        var refund = await RunAsync<PixService, Result<PixChargeView>>(s => s.RefundAsync(fx.AdminUserId, charge.Id, "teste", Ct));
        Assert.Contains("venda ativa", refund.Error);
    }

    [Fact]
    public async Task Paid_pix_without_sale_shows_in_reconciliation_and_can_be_refunded()
    {
        EnableSimulator(paysAfterSeconds: 0);
        var charge = await RunAsync<PixService, PixChargeView>(s => s.CreateChargeAsync(fx.Terminal.Id, Guid.NewGuid(), 7.50m, Ct));

        // Caixa desligou antes de consultar: o acompanhamento do servidor confirma o pagamento.
        Assert.True(await RunAsync<PixService, int>(s => s.RefreshOpenAsync(Ct)) >= 1);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = await RunAsync<PixService, IReadOnlyList<PixChargeRow>>(s => s.ListAsync(fx.AdminUserId, today, today, Ct));
        var row = rows.Single(r => r.Id == charge.Id);
        Assert.True(row.CanRefund);
        Assert.Null(row.SaleNumber);

        // Operador sem permissão não devolve; supervisor/administrador devolve com motivo.
        var denied = await RunAsync<PixService, Result<PixChargeView>>(s => s.RefundAsync(fx.OperatorUserId, charge.Id, "desistiu", Ct));
        Assert.False(denied.Succeeded);
        var refunded = await RunAsync<PixService, Result<PixChargeView>>(s => s.RefundAsync(fx.AdminUserId, charge.Id, "Cliente desistiu da compra", Ct));
        Assert.True(refunded.Succeeded, refunded.Error);
        Assert.Equal(PixChargeStatus.Refunded, refunded.Value!.Status);
        await Assert.ThrowsAsync<DomainException>(() => RunAsync<PixService, IReadOnlyList<PixChargeRow>>(s => s.ListAsync(fx.OperatorUserId, today, today, Ct)));
    }

    [Fact]
    public async Task Cancelling_an_unpaid_pix_removes_it_but_a_paid_one_comes_back_as_paid()
    {
        EnableSimulator(paysAfterSeconds: 3600);
        var open = await RunAsync<PixService, PixChargeView>(s => s.CreateChargeAsync(fx.Terminal.Id, Guid.NewGuid(), 3m, Ct));
        Assert.Equal(PixChargeStatus.Cancelled, (await RunAsync<PixService, PixChargeView>(s => s.CancelAsync(fx.Terminal.Id, open.Id, Ct))).Status);

        EnableSimulator(paysAfterSeconds: 0);
        var late = await RunAsync<PixService, PixChargeView>(s => s.CreateChargeAsync(fx.Terminal.Id, Guid.NewGuid(), 4m, Ct));
        Assert.Equal(PixChargeStatus.Paid, (await RunAsync<PixService, PixChargeView>(s => s.CancelAsync(fx.Terminal.Id, late.Id, Ct))).Status);

        Settings.Current = PixSettings.Disabled;
        Assert.False(await RunAsync<PixService, bool>(s => s.IsEnabledAsync(Ct)));
        await Assert.ThrowsAsync<DomainException>(() =>
            RunAsync<PixService, PixChargeView>(s => s.CreateChargeAsync(fx.Terminal.Id, Guid.NewGuid(), 1m, Ct)));
    }

    [Fact]
    public async Task Tef_card_is_recorded_with_nsu_brand_and_acquirer()
    {
        var card = new CheckoutPayment(PaymentMethod.CreditCard, 14.90m,
            Integration: new PaymentIntegration("123456", "654321", "2", "11222333000181", "Credenciadora"));
        var sale = await RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(Sale(Guid.NewGuid(), card), Ct));
        Assert.True(sale.Succeeded, sale.Error);

        var receipt = await RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(sale.Value!.SaleId, Ct));
        var payment = Assert.Single(receipt!.Payments);
        Assert.True(payment.IsIntegrated);
        Assert.Equal("02", payment.CardBrand); // normalizado para o código da NFC-e
        Assert.Equal("NSU 123456 AUT 654321", payment.Detail);

        // Integração só vale para cartão; NSU é obrigatório.
        var wrong = new CheckoutPayment(PaymentMethod.Cash, 50m, Integration: new PaymentIntegration("1", null, null, null, null));
        Assert.False((await RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(Sale(Guid.NewGuid(), wrong), Ct))).Succeeded);
    }

    [Fact]
    public async Task Pix_settings_validate_https_cnpj_and_required_credentials()
    {
        var store = Settings;
        store.Current = PixSettings.Disabled;
        Task<Result<PixConfigView>> Save(PixConfigUpdate u) => RunAsync<PixConfigurationService, Result<PixConfigView>>(s => s.SaveAsync(fx.AdminUserId, u, Ct));
        var valid = new PixConfigUpdate(true, PixSettings.BacenApi, "https://api.banco.example/v2", null, "cliente", "segredo", null, null,
            "pix@loja.example", "11.222.333/0001-81", "Banco Teste", 300);

        Assert.Contains("https", (await Save(valid with { BaseUrl = "http://api.banco.example/v2" })).Error);
        Assert.Contains("CNPJ", (await Save(valid with { PspCnpj = "11.222.333/0001-00" })).Error);
        Assert.Contains("client secret", (await Save(valid with { ClientSecret = null })).Error);
        var saved = await Save(valid);
        Assert.True(saved.Succeeded, saved.Error);
        Assert.True(saved.Value!.HasClientSecret);
        Assert.Equal("11222333000181", store.Current.PspCnpj);

        // Segredo vazio mantém o salvo.
        Assert.True((await Save(valid with { ClientSecret = null })).Succeeded);
        Assert.Equal("segredo", store.Current.ClientSecret);
        await Assert.ThrowsAsync<DomainException>(() => RunAsync<PixConfigurationService, PixConfigView>(s => s.GetAsync(fx.OperatorUserId, Ct)));
        store.Current = PixSettings.Disabled;
    }
}

public class PixProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PixSettings Settings = PixSettings.Disabled with
    {
        Enabled = true, Provider = PixSettings.BacenApi, BaseUrl = "https://psp.example/v2", TokenUrl = "https://psp.example/oauth/token",
        ClientId = "id", ClientSecret = "secret", PixKey = "pix@loja.example",
    };

    [Fact]
    public void Br_code_crc_matches_the_central_bank_example()
    {
        // Exemplo do manual do BR Code (Banco Central).
        Assert.True(BrCode.IsValid("00020126580014br.gov.bcb.pix0136123e4567-e12b-12d1-a456-4266554400005204000053039865802BR5913Fulano de Tal6008BRASILIA62070503***63041D3D"));
        var code = BrCode.Dynamic("pix.example/qr/v2/abc", "Loja São João", "São Paulo", 10.5m);
        Assert.True(BrCode.IsValid(code));
        Assert.Contains("5913LOJA SAO JOAO", code);
        Assert.Contains("540510.50", code);
    }

    [Fact]
    public async Task Standard_api_authenticates_creates_queries_and_refunds()
    {
        var psp = new FakePsp();
        using var provider = new BacenPixProvider(_ => psp);

        var created = await provider.CreateAsync(new PspChargeRequest("abcdefghijklmnopqrstuvwxyz12", 12.34m, 300, "Compra"), Settings, Ct);
        Assert.Equal("000201COPIAECOLA", created.CopyPaste);
        var put = psp.Requests.Single(r => r.Method == "PUT");
        Assert.EndsWith("/v2/cob/abcdefghijklmnopqrstuvwxyz12", put.Url);
        Assert.Equal("Bearer tok-1", put.Authorization);
        Assert.Equal("12.34", JsonNode.Parse(put.Body)!["valor"]!["original"]!.GetValue<string>());
        Assert.Equal("pix@loja.example", JsonNode.Parse(put.Body)!["chave"]!.GetValue<string>());
        Assert.StartsWith("Basic ", psp.Requests[0].Authorization);

        psp.Paid = true;
        var state = await provider.GetAsync("abcdefghijklmnopqrstuvwxyz12", Settings, Ct);
        Assert.Equal(PspChargeStatus.Paid, state.Status);
        Assert.Equal("E12345678202610141530abcdefghijk", state.EndToEndId);
        Assert.Equal(12.34m, state.PaidAmount);
        Assert.Equal(1, psp.Requests.Count(r => r.Url.EndsWith("/oauth/token", StringComparison.Ordinal))); // token reaproveitado

        Assert.Equal(PspRefundStatus.Refunded, await provider.RefundAsync("E123", "D1", 12.34m, Settings, Ct));
        Assert.Contains(psp.Requests, r => r.Method == "PUT" && r.Url.EndsWith("/pix/E123/devolucao/D1", StringComparison.Ordinal));

        psp.Fail = true;
        var ex = await Assert.ThrowsAsync<PixProviderException>(() => provider.GetAsync("x", Settings, Ct));
        Assert.Contains("Cobrança não encontrada", ex.Message);
    }

    private sealed class FakePsp : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Authorization, string Body)> Requests { get; } = [];
        public bool Paid { get; set; }
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.ToString();
            Requests.Add((request.Method.Method, url, request.Headers.Authorization?.ToString(), body));
            if (url.EndsWith("/oauth/token", StringComparison.Ordinal)) return Json("""{"access_token":"tok-1","expires_in":3600}""");
            if (Fail) return Json("""{"title":"Cobrança não encontrada","detail":"Cobrança não encontrada para o txid informado."}""", HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Put && url.Contains("/cob/", StringComparison.Ordinal))
                return Json("""{"status":"ATIVA","calendario":{"criacao":"2026-10-14T18:30:00Z","expiracao":300},"pixCopiaECola":"000201COPIAECOLA"}""", HttpStatusCode.Created);
            if (request.Method == HttpMethod.Get)
                return Json(Paid
                    ? """{"status":"CONCLUIDA","pix":[{"endToEndId":"E12345678202610141530abcdefghijk","valor":"12.34","horario":"2026-10-14T18:31:00Z","pagador":{"nome":"Fulano"}}]}"""
                    : """{"status":"ATIVA"}""");
            if (url.Contains("/devolucao/", StringComparison.Ordinal)) return Json("""{"status":"DEVOLVIDO"}""", HttpStatusCode.Created);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
