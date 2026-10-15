using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Cash;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;
using Pdv.Domain.Sales;

namespace Pdv.Application.Sales;

public sealed record CheckoutItem(Guid ProductId, decimal Quantity, decimal Discount = 0);

/// <param name="Reference">Para vale-troca: o código do vale.</param>
/// <param name="Integration">Cartão aprovado pelo TEF do caixa (NSU, autorização, bandeira, credenciadora).</param>
/// <param name="PixChargeId">Pix com QR dinâmico: a cobrança confirmada pelo PSP.</param>
public sealed record CheckoutPayment(PaymentMethod Method, decimal Amount, string? Reference = null,
    PaymentIntegration? Integration = null, Guid? PixChargeId = null);

/// <param name="ClientRequestId">Gerado pelo terminal quando o carrinho é iniciado; reenviar a mesma chave é seguro.</param>
public sealed record FinalizeSaleCommand(
    Guid ClientRequestId,
    Guid TerminalId,
    Guid OperatorUserId,
    IReadOnlyList<CheckoutItem> Items,
    IReadOnlyList<CheckoutPayment> Payments,
    Guid? DiscountAuthorizedByUserId = null,
    string? CustomerDocument = null,
    string? CustomerName = null);

public sealed record SaleReceipt(Guid SaleId, long Number, decimal Total, decimal Paid, decimal Change, bool AlreadyProcessed);

public sealed class CheckoutService(IPdvDbContext db, PermissionChecker permissions, Payments.IPixSettingsStore pixSettings, TimeProvider clock)
{
    public async Task<Result<SaleReceipt>> FinalizeAsync(FinalizeSaleCommand command, CancellationToken ct = default)
    {
        var existing = await FindExistingAsync(command.ClientRequestId, ct);
        if (existing is not null) return Result<SaleReceipt>.Ok(existing);

        try
        {
            var sale = await BuildSaleAsync(command, ct);

            // Venda, itens, pagamentos, estoque, caixa e auditoria em um único SaveChanges = uma transação.
            await db.SaveChangesAsync(ct);
            return Result<SaleReceipt>.Ok(new SaleReceipt(sale.Id, sale.Number, sale.Total, sale.PaidTotal, sale.ChangeAmount, false));
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<SaleReceipt>.Fail(ex.Message);
        }
        catch (DbUpdateException ex)
        {
            // Solicitação concorrente/repetida com a mesma chave venceu a corrida (índice único em ClientRequestId).
            db.ResetChangeTracker();
            existing = await FindExistingAsync(command.ClientRequestId, ct);
            if (existing is not null) return Result<SaleReceipt>.Ok(existing);

            // A sessão de caixa mudou durante a venda (ex.: fechamento simultâneo).
            if (ex is DbUpdateConcurrencyException)
                return Result<SaleReceipt>.Fail("O caixa foi alterado ou fechado durante a venda. Verifique o caixa e tente novamente.");
            throw;
        }
    }

    /// <param name="userId">Quem autorizou o cancelamento (verificado no banco).</param>
    public async Task<Result<Guid>> CancelAsync(Guid saleId, string reason, Guid userId, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(userId, Permissions.CancelSale, ct))
            return Result<Guid>.Fail("Usuário sem permissão para cancelar vendas.");

        var sale = await db.Sales.Include(s => s.Items).Include(s => s.Payments).SingleOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null) return Result<Guid>.Fail("Venda não encontrada.");

        try
        {
            var now = clock.GetUtcNow();
            sale.Cancel(reason, userId, now);

            // Estorno por lançamentos compensatórios; nada é apagado.
            foreach (var item in sale.Items)
                db.StockMovements.Add(StockMovement.ForSaleCancellation(item.ProductId, item.Quantity, sale.Id, userId, now));

            if (sale.NetCashReceived > 0)
            {
                var session = await db.CashSessions.Include(s => s.Movements)
                    .SingleOrDefaultAsync(s => s.TerminalId == sale.TerminalId && s.Status == CashSessionStatus.Open, ct)
                    ?? throw new DomainException("Abra o caixa para devolver o dinheiro da venda.");
                session.RegisterCashRefund(sale.NetCashReceived, sale.Id, userId, now);
            }

            db.AuditEntries.Add(new AuditEntry(AuditActions.SaleCancelled, userId, sale.TerminalId, sale.Id, reason, now));
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Ok(sale.Id);
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail(ex.Message);
        }
    }

    private async Task<Sale> BuildSaleAsync(FinalizeSaleCommand command, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var session = await db.CashSessions
            .SingleOrDefaultAsync(s => s.TerminalId == command.TerminalId && s.Status == CashSessionStatus.Open, ct)
            ?? throw new DomainException("Não há caixa aberto neste terminal.");

        var sale = new Sale(command.ClientRequestId, command.TerminalId, session.Id, command.OperatorUserId, now);

        var productIds = command.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        // Tributação: perfil do produto ou, se não houver, o padrão da empresa do terminal.
        var defaultProfileId = await (
            from t in db.Terminals
            join s in db.Stores on t.StoreId equals s.Id
            join c in db.Companies on s.CompanyId equals c.Id
            where t.Id == command.TerminalId
            select c.DefaultTaxProfileId).FirstOrDefaultAsync(ct);
        var profileIds = products.Values.Select(p => p.TaxProfileId ?? defaultProfileId).OfType<Guid>().Distinct().ToList();
        var profiles = await db.TaxProfiles.AsNoTracking().Where(t => profileIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        foreach (var item in command.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product) || !product.IsActive)
                throw new DomainException("Produto inexistente ou inativo na venda.");
            var fiscal = (product.TaxProfileId ?? defaultProfileId) is { } pid && profiles.TryGetValue(pid, out var profile)
                ? ItemFiscalData.From(product.Ncm, product.Cest, product.Origin, profile)
                : null;
            sale.AddItem(product.Id, product.Name, product.Unit, item.Quantity, product.SalePrice, item.Discount, fiscal, product.CostPrice);
        }

        sale.SetCustomer(command.CustomerDocument, command.CustomerName);

        if (sale.DiscountTotal > 0)
        {
            var authorizerId = command.DiscountAuthorizedByUserId ?? command.OperatorUserId;
            if (!await permissions.HasAsync(authorizerId, Permissions.ApplyDiscount, ct))
                throw new DomainException("Desconto não autorizado para este usuário.");
            sale.SetDiscountAuthorizer(authorizerId);
            db.AuditEntries.Add(new AuditEntry(AuditActions.DiscountAuthorized, authorizerId, command.TerminalId, sale.Id,
                $"{sale.DiscountTotal:N2}", now));
        }

        foreach (var payment in command.Payments)
        {
            if (payment.Method == PaymentMethod.ExchangeVoucher)
            {
                // O saldo do vale é baixado na mesma transação da venda (e protegido contra uso simultâneo).
                var code = ExchangeVoucher.NormalizeCode(payment.Reference);
                var voucher = await db.ExchangeVouchers.SingleOrDefaultAsync(v => v.Code == code, ct)
                    ?? throw new DomainException($"Vale-troca {payment.Reference} não encontrado.");
                voucher.Redeem(payment.Amount, now);
                sale.AddPayment(payment.Method, payment.Amount, voucher.Code);
            }
            else if (payment.PixChargeId is { } chargeId)
            {
                // Pix dinâmico: só entra confirmado pelo PSP, com o mesmo valor, e quita uma única venda.
                if (payment.Method != PaymentMethod.Pix) throw new DomainException("Cobrança Pix em pagamento que não é Pix.");
                var charge = await db.PixCharges.SingleOrDefaultAsync(c => c.Id == chargeId, ct)
                    ?? throw new DomainException("Cobrança Pix não encontrada.");
                charge.LinkToSale(sale.Id, payment.Amount);
                var psp = pixSettings.Load();
                sale.AddPayment(PaymentMethod.Pix, payment.Amount, charge.Txid,
                    new PaymentIntegration(charge.EndToEndId!, null, null, psp.PspCnpj, psp.PspName));
            }
            else if (payment.Integration is { } tef)
            {
                if (payment.Method is not (PaymentMethod.CreditCard or PaymentMethod.DebitCard))
                    throw new DomainException("Só pagamentos com cartão podem vir do TEF.");
                if (string.IsNullOrWhiteSpace(tef.TransactionId)) throw new DomainException("Pagamento com cartão sem NSU.");
                sale.AddPayment(payment.Method, payment.Amount, payment.Reference,
                    tef with { CardBrand = PaymentIntegration.NormalizeBrand(tef.CardBrand) });
            }
            else
            {
                sale.AddPayment(payment.Method, payment.Amount, payment.Reference);
            }
        }

        sale.Complete(now);

        db.Sales.Add(sale);
        foreach (var item in sale.Items)
            db.StockMovements.Add(StockMovement.ForSale(item.ProductId, item.Quantity, sale.Id, command.OperatorUserId, now));
        session.RegisterSale(sale, command.OperatorUserId, now);
        db.AuditEntries.Add(new AuditEntry(AuditActions.SaleCompleted, command.OperatorUserId, command.TerminalId, sale.Id,
            $"{sale.Total:N2}", now));

        return sale;
    }

    private Task<SaleReceipt?> FindExistingAsync(Guid clientRequestId, CancellationToken ct) =>
        db.Sales.AsNoTracking()
            .Where(s => s.ClientRequestId == clientRequestId)
            .Select(s => new SaleReceipt(s.Id, s.Number, s.Total, s.Payments.Sum(p => p.Amount), s.ChangeAmount, true))
            .FirstOrDefaultAsync(ct);
}
