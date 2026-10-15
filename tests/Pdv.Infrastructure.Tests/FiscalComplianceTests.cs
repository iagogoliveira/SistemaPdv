using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Fiscal;
using Pdv.Application.Sales;
using Pdv.Domain.Fiscal;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

/// <summary>Tributos aproximados (IBPT / Lei 12.741) e inutilização de numeração.</summary>
[Collection(nameof(NfceGroup))]
public class FiscalComplianceTests(NfceFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Formato da tabela "De olho no imposto" (IBPT): separador ";", vigência dd/MM/aaaa.
    private const string IbptCsv = """
        codigo;ex;tipo;descricao;nacionalfederal;importadosfederal;estadual;municipal;vigenciainicio;vigenciafim;chave;versao;fonte
        48202000;;0;Cadernos;13.45;17.42;18.00;0.00;01/09/2026;31/12/2026;ABC123;26.2.C;IBPT/empresometro.com.br
        48202000;01;0;Cadernos (ex 01);20.00;20.00;20.00;0.00;01/09/2026;31/12/2026;ABC123;26.2.C;IBPT/empresometro.com.br
        96081000;;0;Canetas esferograficas;14.00;18.00;18.00;0.00;01/09/2026;31/12/2026;ABC123;26.2.C;IBPT/empresometro.com.br
        0101;;1;Servico (NBS);0;0;0;5.00;01/09/2026;31/12/2026;ABC123;26.2.C;IBPT/empresometro.com.br
        """;

    private async Task<T> RunAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        await using var scope = fx.NewScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    [Fact]
    public async Task Ibpt_table_feeds_nfce_vtottrib_and_receipt_text()
    {
        var denied = await RunAsync<TaxApproximationService, Pdv.Application.Result<TaxTableImport>>(s => s.ImportAsync(fx.OperatorUserId, IbptCsv, Ct));
        Assert.False(denied.Succeeded);
        var bad = await RunAsync<TaxApproximationService, Pdv.Application.Result<TaxTableImport>>(s => s.ImportAsync(fx.AdminUserId, "a;b;c\n1;2;3", Ct));
        Assert.Contains("tabela IBPT", bad.Error);

        var imported = await RunAsync<TaxApproximationService, Pdv.Application.Result<TaxTableImport>>(s => s.ImportAsync(fx.AdminUserId, IbptCsv, Ct));
        Assert.True(imported.Succeeded, imported.Error);
        Assert.Equal(2, imported.Value!.Rows);          // duas NCM; a exceção "01" e a linha de serviço ficam de fora
        Assert.Equal("26.2.C", imported.Value.Version);
        var status = await RunAsync<TaxApproximationService, TaxTableStatus>(s => s.GetStatusAsync(Ct));
        Assert.Equal(new DateOnly(2026, 12, 31), status.ValidTo);

        // Caderno de R$ 10,00 (NCM 48202000, nacional): federal 13,45% e estadual 18%.
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        var nfce = await fx.IssueAsync(saleId);
        Assert.True(nfce.Succeeded, nfce.Error);
        var draft = fx.Sefaz.Drafts.Last();
        Assert.Equal(3.15m, Assert.Single(draft.Items).ApproximateTax);
        Assert.Contains("Trib aprox R$ 1,35 Federal, R$ 1,80 Estadual e R$ 0,00 Municipal. Fonte: IBPT/empresometro.com.br 26.2.C", draft.AdditionalInfo);

        var receipt = await RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(saleId, Ct));
        Assert.StartsWith("Trib aprox R$ 1,35 Federal", receipt!.ApproximateTaxes);
    }

    [Fact]
    public void Imported_goods_use_the_imported_federal_rate()
    {
        var rates = new TaxApproximation("96081000", null, "Canetas", 14m, 18m, 18m, 2m, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "1", "IBPT");
        Assert.Equal(new ApproximateTaxes(1.40m, 1.80m, 0.20m), ApproximateTaxes.Compute(rates, ProductOrigin.Nacional, 10m));
        Assert.Equal(1.80m, ApproximateTaxes.Compute(rates, ProductOrigin.EstrangeiraImportacaoDireta, 10m).Federal);
        Assert.Equal(ApproximateTaxes.Zero, ApproximateTaxes.Compute(null, ProductOrigin.Nacional, 10m));
    }

    [Fact]
    public async Task Voiding_numbers_covers_rejected_notes_of_cancelled_sales_and_future_gaps_only()
    {
        // NFC-e rejeitada (ex.: cadastro errado) e a venda cancelada: o número deve ser inutilizado.
        fx.Sefaz.NextAuthorize(new SefazResult(SefazOutcome.Rejected, 778, "Rejeição: NCM inexistente"));
        var saleId = await fx.SellAsync(fx.FiscalProductId);
        var rejected = (await fx.IssueAsync(saleId)).Value!;
        Assert.Equal(FiscalDocumentStatus.Rejected, rejected.Status);

        // Venda ativa: o número não pode ser inutilizado (o certo é corrigir e reenviar).
        var blocked = await RunAsync<NfceService, Pdv.Application.Result<string>>(s =>
            s.VoidNumbersAsync(fx.AdminUserId, rejected.Number, rejected.Number, "Numeracao nao utilizada por erro de cadastro", Ct));
        Assert.Contains("venda ativa", blocked.Error);

        await RunAsync<CheckoutService, Pdv.Application.Result<Guid>>(c => c.CancelAsync(saleId, "Cliente desistiu da compra", fx.AdminUserId, Ct));
        var suggestions = await RunAsync<NfceService, IReadOnlyList<long>>(s => s.GetVoidSuggestionsAsync(fx.AdminUserId, Ct));
        Assert.Contains(rejected.Number, suggestions);

        Assert.Contains("15 a 255", (await RunAsync<NfceService, Pdv.Application.Result<string>>(s =>
            s.VoidNumbersAsync(fx.AdminUserId, rejected.Number, rejected.Number, "curta", Ct))).Error);
        var voided = await RunAsync<NfceService, Pdv.Application.Result<string>>(s =>
            s.VoidNumbersAsync(fx.AdminUserId, rejected.Number, rejected.Number, "Numeracao nao utilizada por erro de cadastro", Ct));
        Assert.True(voided.Succeeded, voided.Error);
        Assert.Contains((1, rejected.Number, rejected.Number, "Numeracao nao utilizada por erro de cadastro"), fx.Sefaz.Voids);

        await using (var scope = fx.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
            Assert.Equal(FiscalDocumentStatus.Voided, (await db.FiscalDocuments.SingleAsync(d => d.Id == rejected.DocumentId, Ct)).Status);
            Assert.Equal(Domain.Fiscal.NumberVoidStatus.Done, (await db.NumberVoids.SingleAsync(v => v.From == rejected.Number, Ct)).Status);
        }
        Assert.DoesNotContain(rejected.Number, await RunAsync<NfceService, IReadOnlyList<long>>(s => s.GetVoidSuggestionsAsync(fx.AdminUserId, Ct)));

        // Nota autorizada nunca pode ser inutilizada.
        var good = (await fx.IssueAsync(await fx.SellAsync(fx.FiscalProductId))).Value!;
        Assert.Contains("emitida", (await RunAsync<NfceService, Pdv.Application.Result<string>>(s =>
            s.VoidNumbersAsync(fx.AdminUserId, good.Number, good.Number, "Tentativa indevida de inutilizar", Ct))).Error);

        // Faixa à frente da numeração: a série pula para depois dela.
        var ahead = good.Number + 5;
        Assert.True((await RunAsync<NfceService, Pdv.Application.Result<string>>(s =>
            s.VoidNumbersAsync(fx.AdminUserId, ahead, ahead + 1, "Numeros reservados por falha no sistema", Ct))).Succeeded);
        var next = (await fx.IssueAsync(await fx.SellAsync(fx.FiscalProductId))).Value!;
        Assert.Equal(ahead + 2, next.Number);
        Assert.Contains(good.Number + 1, await RunAsync<NfceService, IReadOnlyList<long>>(s => s.GetVoidSuggestionsAsync(fx.AdminUserId, Ct)));
    }
}
