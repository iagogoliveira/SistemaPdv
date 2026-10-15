using Microsoft.Extensions.DependencyInjection;
using Pdv.Application;
using Pdv.Application.Cash;
using Pdv.Application.Licensing;
using Pdv.Application.Sales;
using Pdv.Client;
using Pdv.Domain.Common;
using Pdv.Domain.Sales;

namespace Pdv.Server.Tests;

public class LicenseRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private const string Machine = "AAAA-BBBB-CCCC-DDDD";

    private static LicenseData License(DateOnly? expires, string machine = Machine, int terminals = 3) =>
        new("LIC-1", "Loja X", null, "Loja 3 caixas", terminals, new DateOnly(2026, 1, 1), expires, machine);

    [Fact]
    public void Trial_lasts_30_days_with_two_terminals_then_blocks_sales()
    {
        var trial = LicenseRules.Evaluate(null, null, Today, Today.AddDays(-10), Machine, 1);
        Assert.Equal(LicenseState.Trial, trial.State);
        Assert.Equal(20, trial.DaysLeft);
        Assert.Equal(2, trial.MaxTerminals);
        Assert.True(trial.CanSell);
        Assert.Null(trial.Warning);

        Assert.NotNull(LicenseRules.Evaluate(null, null, Today, Today.AddDays(-25), Machine, 1).Warning);
        var ended = LicenseRules.Evaluate(null, null, Today, Today.AddDays(-30), Machine, 1);
        Assert.Equal(LicenseState.Restricted, ended.State);
        Assert.False(ended.CanSell);
    }

    [Fact]
    public void Expiry_warns_then_grace_then_restricted()
    {
        Assert.Equal(LicenseState.Active, LicenseRules.Evaluate(License(null), null, Today, Today, Machine, 3).State);
        var far = LicenseRules.Evaluate(License(Today.AddDays(60)), null, Today, Today, Machine, 3);
        Assert.Null(far.Warning);
        Assert.Contains("vence em 10", LicenseRules.Evaluate(License(Today.AddDays(10)), null, Today, Today, Machine, 3).Warning);

        var grace = LicenseRules.Evaluate(License(Today.AddDays(-3)), null, Today, Today, Machine, 3);
        Assert.Equal(LicenseState.Grace, grace.State);
        Assert.True(grace.CanSell);
        Assert.Equal(12, grace.DaysLeft);

        var blocked = LicenseRules.Evaluate(License(Today.AddDays(-15)), null, Today, Today, Machine, 3);
        Assert.Equal(LicenseState.Restricted, blocked.State);
        Assert.Contains("relatórios e backup continuam", blocked.Message);
    }

    [Fact]
    public void License_from_another_machine_is_restricted()
    {
        var other = LicenseRules.Evaluate(License(null, "ZZZZ-ZZZZ-ZZZZ-ZZZZ"), null, Today, Today, Machine, 1);
        Assert.Equal(LicenseState.Restricted, other.State);
        Assert.Contains("outro computador", other.Message);
        // Formatação do código não importa (hífens, minúsculas).
        Assert.Equal(LicenseState.Active, LicenseRules.Evaluate(License(null, "aaaabbbbccccdddd"), null, Today, Today, Machine, 1).State);
    }

    [Fact]
    public void Signature_detects_tampering_and_foreign_keys()
    {
        var (privatePem, publicPem) = LicenseCodec.NewKeyPair();
        var text = LicenseCodec.Sign(License(null), privatePem);
        Assert.Equal("Loja X", LicenseCodec.Verify(text, publicPem).Customer);

        // Aumentar o número de caixas no arquivo invalida a assinatura.
        var json = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(json["payload"]!.GetValue<string>()));
        json["payload"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload.Replace("\"maxTerminals\":3", "\"maxTerminals\":30", StringComparison.Ordinal)));
        Assert.Throws<DomainException>(() => LicenseCodec.Verify(json.ToJsonString(), publicPem));

        var (_, otherPublic) = LicenseCodec.NewKeyPair();
        Assert.Throws<DomainException>(() => LicenseCodec.Verify(text, otherPublic));
        Assert.Throws<DomainException>(() => LicenseCodec.Verify("não é licença", publicPem));
        Assert.Contains("BEGIN PUBLIC KEY", LicenseKeys.Production);
    }
}

[Collection(nameof(ServerGroup))]
public class LicenseEnforcementTests(ServerFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expired_license_blocks_sales_and_new_terminals_but_not_reports()
    {
        var t = await fx.PairAsync("CAIXA-LICENCA");
        var status = await t.Runner.RunAsync<LicenseService, LicenseStatus>(s => s.GetStatusAsync(fx.AdminUserId, default));
        Assert.Equal(LicenseState.Active, status.State);
        Assert.Equal(ServerFixture.MachineCode, status.MachineCode);
        Assert.Null((await t.Runner.RunAsync<LicenseService, LicenseStatus>(s => s.GetStatusAsync(null, default))).MachineCode);

        try
        {
            // Licença de outra máquina ou adulterada é recusada na instalação.
            var foreign = fx.IssueLicense(5, null, "ZZZZ-ZZZZ-ZZZZ-ZZZZ");
            Assert.Contains("computador", (await t.Runner.RunAsync<LicenseService, Result<LicenseStatus>>(s => s.InstallAsync(fx.AdminUserId, foreign, default))).Error);

            var expired = fx.IssueLicense(50, DateOnly.FromDateTime(DateTime.Today).AddDays(-30));
            var installed = await t.Runner.RunAsync<LicenseService, Result<LicenseStatus>>(s => s.InstallAsync(fx.AdminUserId, expired, default));
            Assert.True(installed.Succeeded, installed.Error);
            Assert.Equal(LicenseState.Restricted, installed.Value!.State);

            var open = await t.Runner.RunAsync<CashService, Result<CashSessionSummary>>(c => c.OpenAsync(t.Info.Id, fx.AdminUserId, 100m, default));
            Assert.Contains("vendas bloqueadas", open.Error);
            var command = new FinalizeSaleCommand(Guid.NewGuid(), t.Info.Id, fx.AdminUserId, [new CheckoutItem(fx.ProductId, 1)],
                [new CheckoutPayment(PaymentMethod.Cash, 50m)]);
            Assert.Contains("vendas bloqueadas", (await t.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(command, default))).Error);
            var newTerminal = await Assert.ThrowsAsync<DomainException>(() => fx.PairAsync("CAIXA-NOVO-SEM-LICENCA"));
            Assert.Contains("vendas bloqueadas", newTerminal.Message);

            // Dados continuam acessíveis.
            Assert.NotNull(await t.Runner.RunAsync<Application.Reports.ReportService, IReadOnlyList<Application.Reports.DailySalesRow>>(r =>
                r.SalesByDayAsync(fx.AdminUserId, new Application.Reports.ReportPeriod(DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today)), default)));

            // Limite de caixas do plano.
            var used = (await t.Runner.RunAsync<LicenseService, LicenseStatus>(s => s.GetStatusAsync(null, default))).UsedTerminals;
            var limited = fx.IssueLicense(used, null);
            Assert.True((await t.Runner.RunAsync<LicenseService, Result<LicenseStatus>>(s => s.InstallAsync(fx.AdminUserId, limited, default))).Succeeded);
            var overLimit = await Assert.ThrowsAsync<DomainException>(() => fx.PairAsync("CAIXA-ALEM-DO-LIMITE"));
            Assert.Contains("Limite da licença", overLimit.Message);
            Assert.Equal(t.Info.Id, (await fx.PairAsync("CAIXA-LICENCA")).Info.Id); // reconectar não conta de novo
        }
        finally
        {
            // Direto no servidor: a chave do caixa do teste foi trocada ao reconectar.
            await using var scope = fx.NewScope();
            var restored = await scope.ServiceProvider.GetRequiredService<LicenseService>().InstallAsync(fx.AdminUserId, fx.IssueLicense(50, null), Ct);
            Assert.True(restored.Succeeded, restored.Error);
        }
    }
}
