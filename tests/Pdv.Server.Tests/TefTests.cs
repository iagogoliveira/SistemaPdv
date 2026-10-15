using Pdv.Client;
using Pdv.Domain.Common;
using Pdv.Domain.Payments;
using Pdv.Domain.Sales;

namespace Pdv.Server.Tests;

public class TefTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Approved_card_stays_pending_until_confirmed_or_undone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdv-tef-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var tef = new TefCoordinator(new SimulatedTefClient(TimeSpan.Zero), new TefPendingStore(folder));
            var messages = new List<string>();
            var progress = new Progress<string>(messages.Add);

            var credit = await tef.PayAsync(new TefRequest(TefCardType.Credit, 25m, 1, "001-x"), progress, Ct);
            var debit = await tef.PayAsync(new TefRequest(TefCardType.Debit, 10m, 1, "001-x"), progress, Ct);
            Assert.True(credit.Approved);
            Assert.Equal("02", credit.Approval!.CardBrand);
            Assert.Equal(SimulatedTefClient.AcquirerCnpj, credit.Approval.AcquirerCnpj);
            Assert.Contains("NSU", credit.Approval.CustomerReceipt);

            // "Queda de energia": um novo coordenador (caixa reiniciado) enxerga as duas pendentes no disco.
            var restarted = new TefCoordinator(new SimulatedTefClient(TimeSpan.Zero), new TefPendingStore(folder));
            Assert.Equal(2, restarted.Pending.Count);
            await restarted.ConfirmAsync([credit.Approval], Ct);
            await restarted.UndoAsync([debit.Approval!], Ct);
            Assert.Empty(restarted.Pending);
            Assert.False(File.Exists(new TefPendingStore(folder).FilePath));

            var declined = await tef.PayAsync(new TefRequest(TefCardType.Credit, 10.13m, 1, "001-x"), progress, Ct);
            Assert.False(declined.Approved);
            Assert.Contains("negada", declined.Message);
            Assert.Empty(tef.Pending);

            var reversal = await tef.ReverseAsync(credit.Approval, progress, Ct);
            Assert.True(reversal.Approved);
            Assert.NotEqual(credit.Approval.Nsu, reversal.Approval!.Nsu);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Integration_details_only_for_card_and_pix()
    {
        var sale = new Sale(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => sale.AddPayment(PaymentMethod.Cash, 10m, null, new PaymentIntegration("1", null, null, null, null)));
        var card = sale.AddPayment(PaymentMethod.DebitCard, 10m, null, new PaymentIntegration("998877", "A1", "06", "11222333000181", "Cred"));
        Assert.True(card.IsIntegrated);
        Assert.Equal("998877", card.TransactionId);
        Assert.Equal("06", PaymentIntegration.NormalizeBrand("6"));
        Assert.Null(PaymentIntegration.NormalizeBrand("VISA"));
    }

    [Fact]
    public void Pix_charge_lifecycle_rules()
    {
        var now = DateTimeOffset.UtcNow;
        var charge = new PixCharge(Guid.NewGuid(), Guid.NewGuid(), 10m, "Simulado", PixCharge.NewTxid(), "000201", now, now.AddMinutes(5));
        Assert.Throws<DomainException>(() => charge.LinkToSale(Guid.NewGuid(), 10m)); // ainda não pago
        Assert.Throws<DomainException>(() => charge.MarkPaid("E1", 9.99m, null, now));  // valor diferente
        charge.MarkPaid("E1", 10m, "Fulano", now);
        Assert.Throws<DomainException>(() => charge.Cancel("x"));
        var sale = Guid.NewGuid();
        charge.LinkToSale(sale, 10m);
        charge.LinkToSale(sale, 10m); // mesma venda (reenvio) é idempotente
        Assert.Throws<DomainException>(() => charge.LinkToSale(Guid.NewGuid(), 10m));
        Assert.False(PixCharge.IsValidTxid("curto"));
        Assert.Throws<DomainException>(() => new PixCharge(Guid.NewGuid(), Guid.NewGuid(), 10m, "S", "txid-com-hifen-nao-vale-xxxxx", "0002", now, now));

        var expired = new PixCharge(Guid.NewGuid(), Guid.NewGuid(), 5m, "S", PixCharge.NewTxid(), "0002", now, now.AddSeconds(1));
        expired.Expire(now.AddSeconds(2));
        Assert.Equal(PixChargeStatus.Expired, expired.Status);
    }
}
