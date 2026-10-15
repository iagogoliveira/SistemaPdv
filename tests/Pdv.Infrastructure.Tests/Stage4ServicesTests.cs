using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Purchases;
using Pdv.Application.Reports;
using Pdv.Application.Sales;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

/// <summary>Gera XML de NF-e (modelo 55) de fornecedor no formato do Portal da NF-e, para testes de importação.</summary>
public static class SupplierInvoiceXml
{
    public const string SupplierCnpj = "11222333000181";

    public static (string Xml, string Key) Build(long number, params (string Code, string? Ean, string Name, string Ncm, string Unit, decimal Qty, decimal Price, decimal Discount)[] items)
    {
        var key = AccessKey.Build("SP", new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(-3)), SupplierCnpj, 55, 1, number,
            FiscalEmissionType.Normal, 10_000_000 + (int)number);
        string D(decimal v) => v.ToString("0.00##", CultureInfo.InvariantCulture);
        var dets = string.Concat(items.Select((it, i) => $"""
            <det nItem="{i + 1}"><prod><cProd>{it.Code}</cProd><cEAN>{it.Ean ?? "SEM GTIN"}</cEAN><xProd>{it.Name}</xProd>
            <NCM>{it.Ncm}</NCM><CFOP>5102</CFOP><uCom>{it.Unit}</uCom><qCom>{D(it.Qty)}</qCom><vUnCom>{D(it.Price)}</vUnCom>
            <vProd>{D(it.Qty * it.Price)}</vProd><cEANTrib>{it.Ean ?? "SEM GTIN"}</cEANTrib><uTrib>{it.Unit}</uTrib><qTrib>{D(it.Qty)}</qTrib>
            <vUnTrib>{D(it.Price)}</vUnTrib>{(it.Discount > 0 ? $"<vDesc>{D(it.Discount)}</vDesc>" : "")}<indTot>1</indTot></prod></det>
            """));
        var total = items.Sum(i => i.Qty * i.Price - i.Discount);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <nfeProc versao="4.00" xmlns="http://www.portalfiscal.inf.br/nfe"><NFe><infNFe versao="4.00" Id="NFe{key}">
            <ide><cUF>35</cUF><natOp>VENDA</natOp><mod>55</mod><serie>1</serie><nNF>{number}</nNF><dhEmi>2026-10-15T09:00:00-03:00</dhEmi></ide>
            <emit><CNPJ>{SupplierCnpj}</CNPJ><xNome>Distribuidora Papel Bom Ltda</xNome></emit>
            <dest><CNPJ>99999999000191</CNPJ><xNome>Cliente</xNome></dest>
            {dets}
            <total><ICMSTot><vNF>{D(total)}</vNF></ICMSTot></total>
            </infNFe></NFe></nfeProc>
            """;
        return (xml, key);
    }
}

[Collection(nameof(DatabaseGroup))]
public class Stage4ServicesTests(DatabaseFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid Id, string Barcode)> NewProductAsync(string name, decimal price, string? barcode = null, string? ncm = null)
    {
        await using var scope = fx.NewScope();
        barcode ??= $"20{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        var result = await scope.ServiceProvider.GetRequiredService<ProductAdminService>().SaveAsync(fx.AdminUserId,
            new ProductEditModel(null, name, "UN", null, price, price / 2, [barcode], ncm), Ct);
        Assert.True(result.Succeeded, result.Error);
        return (result.Value, barcode);
    }

    private async Task<decimal> StockAsync(Guid productId)
    {
        await using var scope = fx.NewScope();
        return await scope.ServiceProvider.GetRequiredService<ProductLookupService>().GetStockAsync(productId, Ct);
    }

    [Fact]
    public async Task Xml_import_links_by_gtin_learns_supplier_codes_and_converts_boxes()
    {
        var gtin = "7891000315507";
        var (byGtin, _) = await NewProductAsync($"Caderno GTIN {Guid.NewGuid():N}"[..20], 19.90m, gtin);
        var (boxed, _) = await NewProductAsync($"Lápis {Guid.NewGuid():N}"[..14], 1.50m);
        var number = Random.Shared.Next(1000, 900_000);
        var (xml, key) = SupplierInvoiceXml.Build(number,
            ("CAD-96", gtin, "CADERNO 96F", "48202000", "UN", 10, 8.20m, 2m),
            ("LAP-CX12", null, "LAPIS PRETO CX C/12", "96091000", "CX", 3, 6.00m, 0));

        await using var scope = fx.NewScope();
        var purchases = scope.ServiceProvider.GetRequiredService<PurchaseService>();
        var preview = await purchases.PreviewXmlAsync(xml, Ct);
        Assert.True(preview.Succeeded, preview.Error);
        var draft = preview.Value!;
        Assert.Equal(byGtin, draft.Lines[0].ProductId);
        Assert.False(draft.Lines[1].IsLinked);
        Assert.Contains(draft.Warnings, w => w.Contains("destinatário", StringComparison.Ordinal));

        // Usuário vincula a caixa ao lápis: 1 CX = 12 UN.
        draft = draft with { Lines = [draft.Lines[0], draft.Lines[1] with { ProductId = boxed, ConversionFactor = 12 }] };
        Assert.False((await purchases.ConfirmAsync(fx.OperatorUserId, draft, Ct)).Succeeded);
        var confirmed = await purchases.ConfirmAsync(fx.AdminUserId, draft, Ct);
        Assert.True(confirmed.Succeeded, confirmed.Error);

        Assert.Equal(10m, await StockAsync(byGtin));
        Assert.Equal(36m, await StockAsync(boxed));
        var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
        var lapis = await db.Products.AsNoTracking().SingleAsync(p => p.Id == boxed, Ct);
        Assert.Equal(0.50m, lapis.CostPrice);            // 18,00 / 36
        Assert.Equal("96091000", lapis.Ncm);              // NCM aproveitado da nota
        var caderno = await db.Products.AsNoTracking().SingleAsync(p => p.Id == byGtin, Ct);
        Assert.Equal(8.00m, caderno.CostPrice);           // (82,00 − 2,00) / 10

        // Mesma nota de novo: recusada.
        Assert.False((await purchases.PreviewXmlAsync(xml, Ct)).Succeeded);

        // Próxima nota do mesmo fornecedor: o código LAP-CX12 já vem vinculado, com o fator 12.
        var (xml2, _) = SupplierInvoiceXml.Build(number + 1, ("LAP-CX12", null, "LAPIS PRETO CX C/12", "96091000", "CX", 1, 6.00m, 0));
        var second = (await purchases.PreviewXmlAsync(xml2, Ct)).Value!;
        Assert.Equal(boxed, second.Lines[0].ProductId);
        Assert.Equal(12m, second.Lines[0].StockQuantity);
        Assert.NotNull(second.SupplierId);
        _ = key;
    }

    [Fact]
    public async Task Invalid_xml_is_reported()
    {
        await using var scope = fx.NewScope();
        var purchases = scope.ServiceProvider.GetRequiredService<PurchaseService>();
        Assert.False((await purchases.PreviewXmlAsync("isto não é xml", Ct)).Succeeded);
        Assert.False((await purchases.PreviewXmlAsync("<NFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"/>", Ct)).Succeeded);
    }

    [Fact]
    public async Task Returns_in_cash_and_voucher_restore_stock_and_voucher_pays_next_sale()
    {
        var (product, _) = await NewProductAsync($"Mochila {Guid.NewGuid():N}"[..16], 50m);
        await using (var s = fx.NewScope())
            await s.ServiceProvider.GetRequiredService<ProductAdminService>().RegisterEntryAsync(fx.AdminUserId, product, 10, null, "estoque", Ct);

        await using var scope = fx.NewScope();
        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();
        var sale = await checkout.FinalizeAsync(new FinalizeSaleCommand(Guid.NewGuid(), fx.Terminal.Id, fx.OperatorUserId,
            [new CheckoutItem(product, 3)], [new CheckoutPayment(PaymentMethod.Cash, 150m)]), Ct);
        Assert.True(sale.Succeeded, sale.Error);
        Assert.Equal(7m, await StockAsync(product));

        var returns = scope.ServiceProvider.GetRequiredService<ReturnService>();
        var returnable = (await returns.GetReturnableAsync(sale.Value!.Number, Ct)).Value!;
        Assert.Equal(3m, returnable.Lines[0].Available);

        // Operador sem permissão não devolve.
        Assert.False((await returns.CreateAsync(new CreateReturnCommand(sale.Value.SaleId, new Dictionary<int, decimal> { [1] = 1 },
            RefundMethod.Cash, "Defeito", fx.OperatorUserId, fx.Terminal.Id), Ct)).Succeeded);

        var cash = await returns.CreateAsync(new CreateReturnCommand(sale.Value.SaleId, new Dictionary<int, decimal> { [1] = 1 },
            RefundMethod.Cash, "Defeito no zíper", fx.AdminUserId, fx.Terminal.Id), Ct);
        Assert.True(cash.Succeeded, cash.Error);
        Assert.Equal(50m, cash.Value!.Total);

        var voucher = await returns.CreateAsync(new CreateReturnCommand(sale.Value.SaleId, new Dictionary<int, decimal> { [1] = 2 },
            RefundMethod.ExchangeVoucher, "Troca por outro modelo", fx.AdminUserId, fx.Terminal.Id), Ct);
        Assert.True(voucher.Succeeded, voucher.Error);
        Assert.Equal(100m, voucher.Value!.Total);
        Assert.NotNull(voucher.Value.VoucherCode);
        Assert.Equal(10m, await StockAsync(product));

        Assert.False((await returns.CreateAsync(new CreateReturnCommand(sale.Value.SaleId, new Dictionary<int, decimal> { [1] = 1 },
            RefundMethod.Cash, "De novo", fx.AdminUserId, fx.Terminal.Id), Ct)).Succeeded);

        // Usa 80,00 do vale numa nova compra (mais 20,00 no Pix); depois o saldo de 20,00 não paga 50,00.
        var code = voucher.Value.VoucherCode!.ToLowerInvariant().Insert(4, "-");
        var exchange = await checkout.FinalizeAsync(new FinalizeSaleCommand(Guid.NewGuid(), fx.Terminal.Id, fx.OperatorUserId,
            [new CheckoutItem(product, 2)], [new CheckoutPayment(PaymentMethod.ExchangeVoucher, 80m, code), new CheckoutPayment(PaymentMethod.Pix, 20m)]), Ct);
        Assert.True(exchange.Succeeded, exchange.Error);
        Assert.Equal(20m, (await returns.GetVoucherAsync(code, Ct))!.Balance);

        var overspend = await checkout.FinalizeAsync(new FinalizeSaleCommand(Guid.NewGuid(), fx.Terminal.Id, fx.OperatorUserId,
            [new CheckoutItem(product, 1)], [new CheckoutPayment(PaymentMethod.ExchangeVoucher, 50m, code)]), Ct);
        Assert.False(overspend.Succeeded);
    }

    [Fact]
    public async Task Price_check_low_stock_and_report_permissions()
    {
        var (product, barcode) = await NewProductAsync($"Grampo {Guid.NewGuid():N}"[..15], 3.20m);
        await using var scope = fx.NewScope();
        var admin = scope.ServiceProvider.GetRequiredService<ProductAdminService>();
        await admin.RegisterEntryAsync(fx.AdminUserId, product, 4, null, null, Ct);
        var model = (await admin.GetAsync(product, Ct))!;
        Assert.True((await admin.SaveAsync(fx.AdminUserId, model with { MinimumStock = 5 }, Ct)).Succeeded);

        var check = await scope.ServiceProvider.GetRequiredService<ProductLookupService>().PriceCheckAsync(barcode, Ct);
        Assert.Equal(3.20m, Assert.Single(check).Product.SalePrice);
        Assert.Equal(4m, check[0].Stock);

        var reports = scope.ServiceProvider.GetRequiredService<ReportService>();
        Assert.Contains(await reports.LowStockAsync(fx.AdminUserId, Ct), r => r.ProductId == product && r.Missing == 1);
        await Assert.ThrowsAsync<DomainException>(() => reports.LowStockAsync(fx.OperatorUserId, Ct));
    }
}

/// <summary>
/// Critério de aceite da Etapa 4: um dia completo de loja, do caixa aberto ao fechamento, sem mexer no banco,
/// com caixa, estoque e relatórios fechando entre si.
/// </summary>
public class FullStoreDayTest
{
    [Fact]
    public async Task A_whole_day_closes_without_differences()
    {
        var ct = TestContext.Current.CancellationToken;
        var fx = new DatabaseFixture();
        await fx.InitializeAsync();
        try
        {
            await using var scope = fx.NewScope();
            var sp = scope.ServiceProvider;
            var checkout = sp.GetRequiredService<CheckoutService>();
            var cash = sp.GetRequiredService<CashService>();
            var returns = sp.GetRequiredService<ReturnService>();
            var reports = sp.GetRequiredService<ReportService>();
            var products = sp.GetRequiredService<ProductAdminService>();
            var terminal = fx.Terminal.Id;   // caixa aberto pelo fixture com R$ 100,00

            // Manhã: chega mercadoria pelo XML do fornecedor.
            var (xml, _) = SupplierInvoiceXml.Build(501, ("CAD-96", null, "CADERNO 96F", "48202000", "UN", 20, 8.00m, 0));
            var draft = (await sp.GetRequiredService<PurchaseService>().PreviewXmlAsync(xml, ct)).Value!;
            draft = draft with { Lines = [draft.Lines[0] with { ProductId = fx.ProductId }] };
            var purchase = await sp.GetRequiredService<PurchaseService>().ConfirmAsync(fx.AdminUserId, draft, ct);
            Assert.True(purchase.Succeeded, purchase.Error);

            // Vendas: dinheiro com troco, Pix com desconto liberado, e uma venda cancelada.
            Guid Req() => Guid.NewGuid();
            var s1 = await checkout.FinalizeAsync(new(Req(), terminal, fx.OperatorUserId, [new CheckoutItem(fx.ProductId, 2)],
                [new CheckoutPayment(PaymentMethod.Cash, 50m)]), ct);                                     // 29,80; troco 20,20
            var s2 = await checkout.FinalizeAsync(new(Req(), terminal, fx.OperatorUserId, [new CheckoutItem(fx.ProductId, 1, 0.90m)],
                [new CheckoutPayment(PaymentMethod.Pix, 14.00m)], fx.AdminUserId), ct);                    // 14,00
            var s3 = await checkout.FinalizeAsync(new(Req(), terminal, fx.OperatorUserId, [new CheckoutItem(fx.ProductId, 1)],
                [new CheckoutPayment(PaymentMethod.Cash, 14.90m)]), ct);                                   // cancelada depois
            Assert.True(s1.Succeeded && s2.Succeeded && s3.Succeeded);
            Assert.True((await checkout.CancelAsync(s3.Value!.SaleId, "Cliente desistiu", fx.AdminUserId, ct)).Succeeded);

            // Troca: devolve 1 caderno da venda 1 em vale; o vale paga parte de uma venda.
            var ret = await returns.CreateAsync(new(s1.Value!.SaleId, new Dictionary<int, decimal> { [1] = 1 }, RefundMethod.ExchangeVoucher,
                "Troca por outra cor", fx.AdminUserId, terminal), ct);
            var s4 = await checkout.FinalizeAsync(new(Req(), terminal, fx.OperatorUserId, [new CheckoutItem(fx.ProductId, 2)],
                [new CheckoutPayment(PaymentMethod.ExchangeVoucher, 14.90m, ret.Value!.VoucherCode), new CheckoutPayment(PaymentMethod.Cash, 14.90m)]), ct);
            Assert.True(s4.Succeeded, s4.Error);

            // Sangria e fechamento conferindo o valor esperado.
            Assert.True((await cash.WithdrawAsync(terminal, fx.AdminUserId, 30m, "Depósito no banco", ct)).Succeeded);
            var open = (await cash.GetOpenSessionAsync(terminal, ct))!;
            // 100 + 29,80 (s1) + 0 (s3: 14,90 entrou e saiu no estorno) + 14,90 (s4) − 30 = 114,70
            Assert.Equal(114.70m, open.ExpectedCash);
            var closed = await cash.CloseAsync(terminal, fx.OperatorUserId, 114.70m, ct);
            Assert.Equal(0m, closed.Value!.Difference);

            // Relatórios batem com o dia.
            var today = DateOnly.FromDateTime(DateTime.Now);
            var day = Assert.Single(await reports.SalesByDayAsync(fx.AdminUserId, new ReportPeriod(today, today), ct));
            Assert.Equal(3, day.Sales);
            Assert.Equal(1, day.Cancelled);
            Assert.Equal(29.80m + 14.00m + 29.80m, day.Net);
            Assert.Equal(14.90m, day.Returns);

            var methods = await reports.PaymentsByMethodAsync(fx.AdminUserId, new ReportPeriod(today, today), ct);
            Assert.Equal(29.80m + 14.90m, methods.Single(m => m.Method == PaymentMethod.Cash).Amount);
            Assert.Equal(14.90m, methods.Single(m => m.Method == PaymentMethod.ExchangeVoucher).Amount);

            var top = Assert.Single(await reports.TopProductsAsync(fx.AdminUserId, new ReportPeriod(today, today), ct: ct));
            Assert.Equal(4m, top.Quantity);                       // 2 + 1 + 2 − 1 devolvido
            Assert.Equal(29.80m + 14.00m + 29.80m - 14.90m, top.Revenue);

            var closing = (await reports.CashClosingAsync(closed.Value.SessionId, ct))!;
            Assert.Equal(0m, closing.Session.Difference);
            Assert.Equal(14.90m, closing.ReturnsInVoucher);
            Assert.Equal(3, closing.SalesCount);

            // Estoque: 100 (fixture) + 20 (XML) − 2 − 1 − 1 + 1 (cancelada) + 1 (devolução) − 2 = 116
            Assert.Equal(116m, await sp.GetRequiredService<ProductLookupService>().GetStockAsync(fx.ProductId, ct));
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }
}
