using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Cash;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;
using Pdv.Domain.Sales;

namespace Pdv.Application.Sales;

public sealed record ReturnableLine(int LineNumber, string Description, string Unit, decimal SoldQuantity, decimal ReturnedQuantity,
    decimal UnitPaid)
{
    public decimal Available => SoldQuantity - ReturnedQuantity;
}

public sealed record ReturnableSale(Guid SaleId, long Number, DateTimeOffset? CompletedAt, decimal Total, IReadOnlyList<ReturnableLine> Lines);

public sealed record CreateReturnCommand(Guid SaleId, IReadOnlyDictionary<int, decimal> Quantities, RefundMethod Method, string Reason,
    Guid AuthorizedByUserId, Guid TerminalId);

public sealed record ReturnReceipt(Guid ReturnId, long SaleNumber, decimal Total, RefundMethod Method, string? VoucherCode,
    DateTimeOffset? VoucherExpiresAt, IReadOnlyList<(string Description, decimal Quantity, decimal Amount)> Items);

public sealed record VoucherInfo(string Code, decimal Amount, decimal Balance, DateTimeOffset ExpiresAt);

/// <summary>Devolução e troca: devolve estoque e o valor em dinheiro (da gaveta) ou em vale-troca.</summary>
public sealed class ReturnService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    public const int VoucherValidityDays = 90;

    public async Task<Result<ReturnableSale>> GetReturnableAsync(long saleNumber, CancellationToken ct = default)
    {
        var sale = await db.Sales.AsNoTracking().Include(s => s.Items).SingleOrDefaultAsync(s => s.Number == saleNumber, ct);
        if (sale is null) return Result<ReturnableSale>.Fail($"Venda nº {saleNumber} não encontrada.");
        if (sale.Status != SaleStatus.Completed) return Result<ReturnableSale>.Fail($"A venda nº {saleNumber} não está concluída (pode ter sido cancelada).");

        var returned = await ReturnedByLineAsync(sale.Id, ct);
        var lines = sale.Items.OrderBy(i => i.LineNumber).Select(i => new ReturnableLine(i.LineNumber, i.Description, i.Unit, i.Quantity,
            returned.TryGetValue(i.LineNumber, out var r) ? r.Quantity : 0, Money.Round(i.Total / i.Quantity))).ToList();
        return Result<ReturnableSale>.Ok(new ReturnableSale(sale.Id, sale.Number, sale.CompletedAt, sale.Total, lines));
    }

    public async Task<Result<ReturnReceipt>> CreateAsync(CreateReturnCommand command, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(command.AuthorizedByUserId, Permissions.CancelSale, ct))
            return Result<ReturnReceipt>.Fail("Devolução exige liberação de um supervisor.");
        try
        {
            var now = clock.GetUtcNow();
            var sale = await db.Sales.AsNoTracking().Include(s => s.Items).SingleOrDefaultAsync(s => s.Id == command.SaleId, ct)
                ?? throw new DomainException("Venda não encontrada.");
            var returned = await ReturnedByLineAsync(sale.Id, ct);

            var saleReturn = SaleReturn.Create(sale, command.Quantities, returned, command.Method, command.Reason,
                command.AuthorizedByUserId, command.TerminalId, now);

            ExchangeVoucher? voucher = null;
            if (command.Method == RefundMethod.Cash)
            {
                var session = await db.CashSessions.Include(s => s.Movements)
                    .SingleOrDefaultAsync(s => s.TerminalId == command.TerminalId && s.Status == CashSessionStatus.Open, ct)
                    ?? throw new DomainException("Abra o caixa para devolver em dinheiro.");
                if (saleReturn.Total > session.ExpectedCash)
                    throw new DomainException($"Não há dinheiro suficiente na gaveta ({session.ExpectedCash:C}). Use vale-troca ou faça um suprimento.");
                session.RegisterCashRefund(saleReturn.Total, saleReturn.Id, command.AuthorizedByUserId, now);
            }
            else
            {
                voucher = new ExchangeVoucher(saleReturn.Total, now, now.AddDays(VoucherValidityDays));
                db.ExchangeVouchers.Add(voucher);
                saleReturn.AttachVoucher(voucher);
            }

            db.SaleReturns.Add(saleReturn);
            foreach (var item in saleReturn.Items)
                db.StockMovements.Add(StockMovement.ForSaleReturn(item.ProductId, item.Quantity, saleReturn.Id, command.AuthorizedByUserId, now));
            db.AuditEntries.Add(new AuditEntry("sale.returned", command.AuthorizedByUserId, command.TerminalId, saleReturn.Id,
                $"Venda {sale.Number}: {saleReturn.Total:N2} em {(voucher is null ? "dinheiro" : "vale " + voucher.Code)} — {command.Reason}", now));
            await db.SaveChangesAsync(ct);

            return Result<ReturnReceipt>.Ok(new ReturnReceipt(saleReturn.Id, sale.Number, saleReturn.Total, saleReturn.RefundMethod,
                voucher?.Code, voucher?.ExpiresAt, saleReturn.Items.Select(i => (i.Description, i.Quantity, i.RefundAmount)).ToList()));
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<ReturnReceipt>.Fail(ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ResetChangeTracker();
            return Result<ReturnReceipt>.Fail("O caixa foi alterado durante a devolução. Tente novamente.");
        }
    }

    public async Task<VoucherInfo?> GetVoucherAsync(string code, CancellationToken ct = default)
    {
        var normalized = ExchangeVoucher.NormalizeCode(code);
        return await db.ExchangeVouchers.AsNoTracking().Where(v => v.Code == normalized)
            .Select(v => new VoucherInfo(v.Code, v.Amount, v.Balance, v.ExpiresAt)).SingleOrDefaultAsync(ct);
    }

    private async Task<Dictionary<int, (decimal Quantity, decimal Amount)>> ReturnedByLineAsync(Guid saleId, CancellationToken ct)
    {
        var rows = await db.SaleReturns.AsNoTracking().Where(r => r.SaleId == saleId).SelectMany(r => r.Items)
            .GroupBy(i => i.SaleLineNumber)
            .Select(g => new { Line = g.Key, Quantity = g.Sum(i => i.Quantity), Amount = g.Sum(i => i.RefundAmount) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Line, r => (r.Quantity, r.Amount));
    }
}
