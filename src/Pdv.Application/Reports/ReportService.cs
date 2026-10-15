using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Application.Sales;
using Pdv.Domain.Cash;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;
using Pdv.Domain.Sales;

namespace Pdv.Application.Reports;

/// <summary>Período em datas locais da loja (inclusive nas duas pontas).</summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To)
{
    public DateTimeOffset StartUtc(TimeZoneInfo zone) => ToUtc(From, zone);
    public DateTimeOffset EndUtc(TimeZoneInfo zone) => ToUtc(To.AddDays(1), zone);

    private static DateTimeOffset ToUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

public sealed record DailySalesRow(DateOnly Date, int Sales, decimal Gross, decimal Discounts, decimal Net, int Cancelled,
    decimal CancelledTotal, decimal Returns)
{
    /// <summary>Faturamento líquido: vendas válidas − devoluções.</summary>
    public decimal Revenue => Net - Returns;

    public decimal AverageTicket => Sales == 0 ? 0 : Money.Round(Net / Sales);
}

public sealed record PaymentMethodRow(PaymentMethod Method, int Count, decimal Amount)
{
    public string Label => PaymentLabels.For(Method);
}

public sealed record TopProductRow(Guid ProductId, string Description, decimal Quantity, decimal Revenue, decimal Cost)
{
    public decimal Margin => Revenue - Cost;
    public decimal? MarginPercent => Revenue == 0 ? null : Money.Round(Margin / Revenue * 100);
}

public sealed record CashSessionRow(Guid SessionId, string Terminal, string OpenedBy, DateTimeOffset OpenedAt, string? ClosedBy,
    DateTimeOffset? ClosedAt, decimal Opening, decimal Supplies, decimal Withdrawals, decimal CashSales, decimal CashRefunds,
    decimal Expected, decimal? Counted, decimal? Difference, CashSessionStatus Status);

public sealed record OperatorDivergenceRow(string Operator, int Sessions, int SessionsWithDifference, decimal Shortage, decimal Surplus)
{
    public decimal Net => Surplus - Shortage;
}

public sealed record LowStockRow(Guid ProductId, string Name, string Unit, decimal Stock, decimal Minimum)
{
    public decimal Missing => Minimum - Stock;
}

public sealed record CashClosingReport(CashSessionRow Session, IReadOnlyList<PaymentMethodRow> SalesByMethod, int SalesCount,
    int CancelledCount, decimal ReturnsInVoucher, IReadOnlyList<(DateTimeOffset At, string Type, decimal Amount, string? Reason, string User)> Movements);

/// <summary>Relatórios operacionais. Exigem a permissão "reports.view" (Supervisor e Administrador).</summary>
public sealed class ReportService(IPdvDbContext db, PermissionChecker permissions)
{
    private static TimeZoneInfo Zone => TimeZoneInfo.Local;

    public async Task<IReadOnlyList<DailySalesRow>> SalesByDayAsync(Guid userId, ReportPeriod period, CancellationToken ct = default)
    {
        await RequireAsync(userId, ct);
        var (start, end) = (period.StartUtc(Zone), period.EndUtc(Zone));
        var sales = await db.Sales.AsNoTracking()
            .Where(s => s.CompletedAt >= start && s.CompletedAt < end && s.Status != SaleStatus.Draft)
            .Select(s => new { s.CompletedAt, s.Status, s.Subtotal, s.DiscountTotal, s.Total }).ToListAsync(ct);
        var returns = await db.SaleReturns.AsNoTracking().Where(r => r.CreatedAt >= start && r.CreatedAt < end)
            .Select(r => new { r.CreatedAt, Total = r.Items.Sum(i => i.RefundAmount) }).ToListAsync(ct);

        var days = Enumerable.Range(0, period.To.DayNumber - period.From.DayNumber + 1).Select(period.From.AddDays);
        return days.Select(day =>
        {
            var daySales = sales.Where(s => LocalDate(s.CompletedAt!.Value) == day).ToList();
            var valid = daySales.Where(s => s.Status == SaleStatus.Completed).ToList();
            var cancelled = daySales.Where(s => s.Status == SaleStatus.Cancelled).ToList();
            return new DailySalesRow(day, valid.Count, valid.Sum(s => s.Subtotal), valid.Sum(s => s.DiscountTotal), valid.Sum(s => s.Total),
                cancelled.Count, cancelled.Sum(s => s.Total), returns.Where(r => LocalDate(r.CreatedAt) == day).Sum(r => r.Total));
        }).ToList();
    }

    public async Task<IReadOnlyList<PaymentMethodRow>> PaymentsByMethodAsync(Guid userId, ReportPeriod period, CancellationToken ct = default)
    {
        await RequireAsync(userId, ct);
        var (start, end) = (period.StartUtc(Zone), period.EndUtc(Zone));
        return await PaymentsAsync(db.Sales.Where(s => s.CompletedAt >= start && s.CompletedAt < end && s.Status == SaleStatus.Completed), ct);
    }

    /// <summary>Mais vendidos com margem estimada (custo gravado na venda; vendas antigas usam o custo atual).</summary>
    public async Task<IReadOnlyList<TopProductRow>> TopProductsAsync(Guid userId, ReportPeriod period, int take = 50, CancellationToken ct = default)
    {
        await RequireAsync(userId, ct);
        var (start, end) = (period.StartUtc(Zone), period.EndUtc(Zone));
        var sold = await db.Sales.AsNoTracking()
            .Where(s => s.CompletedAt >= start && s.CompletedAt < end && s.Status == SaleStatus.Completed)
            .SelectMany(s => s.Items)
            .Join(db.Products, i => i.ProductId, p => p.Id, (i, p) => new { i.ProductId, p.Name, i.Quantity, i.Total, Cost = i.UnitCost ?? p.CostPrice })
            .ToListAsync(ct);
        var returned = await db.SaleReturns.AsNoTracking()
            .Where(r => r.CreatedAt >= start && r.CreatedAt < end)
            .SelectMany(r => r.Items)
            .GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(i => i.Quantity), Amount = g.Sum(i => i.RefundAmount) })
            .ToDictionaryAsync(x => x.ProductId, ct);

        return sold.GroupBy(i => (i.ProductId, i.Name)).Select(g =>
            {
                var unitCost = g.Sum(i => i.Cost * i.Quantity) / Math.Max(g.Sum(i => i.Quantity), 0.001m);
                var back = returned.GetValueOrDefault(g.Key.ProductId);
                var qty = g.Sum(i => i.Quantity) - (back?.Quantity ?? 0);
                return new TopProductRow(g.Key.ProductId, g.Key.Name, qty, g.Sum(i => i.Total) - (back?.Amount ?? 0), Money.Round(unitCost * qty));
            })
            .OrderByDescending(r => r.Revenue).Take(take).ToList();
    }

    public async Task<IReadOnlyList<CashSessionRow>> CashSessionsAsync(Guid userId, ReportPeriod period, CancellationToken ct = default)
    {
        await RequireAsync(userId, ct);
        var (start, end) = (period.StartUtc(Zone), period.EndUtc(Zone));
        var sessions = await db.CashSessions.AsNoTracking().Include(s => s.Movements)
            .Where(s => s.OpenedAt >= start && s.OpenedAt < end).OrderBy(s => s.OpenedAt).ToListAsync(ct);
        return await ToRowsAsync(sessions, ct);
    }

    public async Task<IReadOnlyList<OperatorDivergenceRow>> DivergencesByOperatorAsync(Guid userId, ReportPeriod period, CancellationToken ct = default)
    {
        var rows = await CashSessionsAsync(userId, period, ct);
        return rows.Where(r => r.Status == CashSessionStatus.Closed)
            .GroupBy(r => r.ClosedBy ?? r.OpenedBy)
            .Select(g => new OperatorDivergenceRow(g.Key, g.Count(), g.Count(r => r.Difference != 0),
                -g.Where(r => r.Difference < 0).Sum(r => r.Difference!.Value), g.Where(r => r.Difference > 0).Sum(r => r.Difference!.Value)))
            .OrderByDescending(r => r.Shortage).ToList();
    }

    public async Task<IReadOnlyList<LowStockRow>> LowStockAsync(Guid userId, CancellationToken ct = default)
    {
        await RequireAsync(userId, ct);
        var rows = await db.Products.AsNoTracking().Where(p => p.IsActive && p.MinimumStock != null)
            .Select(p => new { p.Id, p.Name, p.Unit, Minimum = p.MinimumStock!.Value,
                Stock = db.StockMovements.Where(m => m.ProductId == p.Id).Sum(m => (decimal?)m.Quantity) ?? 0 })
            .ToListAsync(ct);
        return rows.Where(r => r.Stock <= r.Minimum).OrderBy(r => r.Stock - r.Minimum)
            .Select(r => new LowStockRow(r.Id, r.Name, r.Unit, r.Stock, r.Minimum)).ToList();
    }

    /// <summary>
    /// Relatório de fechamento de uma sessão de caixa (impresso ao fechar). Não exige "reports.view":
    /// o próprio operador recebe o resumo do seu caixa.
    /// </summary>
    public async Task<CashClosingReport?> CashClosingAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.CashSessions.AsNoTracking().Include(s => s.Movements).SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;
        var row = (await ToRowsAsync([session], ct))[0];
        var sales = db.Sales.Where(s => s.CashSessionId == sessionId);
        var byMethod = await PaymentsAsync(sales.Where(s => s.Status == SaleStatus.Completed), ct);
        var salesCount = await sales.CountAsync(s => s.Status == SaleStatus.Completed, ct);
        var cancelled = await sales.CountAsync(s => s.Status == SaleStatus.Cancelled, ct);
        var voucherReturns = await db.SaleReturns.Where(r => r.TerminalId == session.TerminalId && r.RefundMethod == RefundMethod.ExchangeVoucher
                && r.CreatedAt >= session.OpenedAt && (session.ClosedAt == null || r.CreatedAt <= session.ClosedAt))
            .SelectMany(r => r.Items).SumAsync(i => (decimal?)i.RefundAmount, ct) ?? 0;

        var userIds = session.Movements.Select(m => m.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var movements = session.Movements.OrderBy(m => m.OccurredAt)
            .Select(m => (m.OccurredAt, MovementLabel(m.Type), m.SignedAmount, m.Reason, users.GetValueOrDefault(m.UserId, "?")))
            .ToList();
        return new CashClosingReport(row, byMethod, salesCount, cancelled, voucherReturns, movements);
    }

    public static string MovementLabel(CashMovementType type) => type switch
    {
        CashMovementType.Opening => "Abertura",
        CashMovementType.Supply => "Suprimento",
        CashMovementType.Withdrawal => "Sangria",
        CashMovementType.CashSale => "Venda em dinheiro",
        CashMovementType.CashRefund => "Devolução/estorno",
        _ => type.ToString(),
    };

    private static async Task<IReadOnlyList<PaymentMethodRow>> PaymentsAsync(IQueryable<Sale> sales, CancellationToken ct)
    {
        var payments = await sales.AsNoTracking().SelectMany(s => s.Payments).GroupBy(p => p.Method)
            .Select(g => new { Method = g.Key, Count = g.Count(), Amount = g.Sum(p => p.Amount) }).ToListAsync(ct);
        // Dinheiro líquido: o troco sai do valor recebido.
        var change = await sales.AsNoTracking().SumAsync(s => (decimal?)s.ChangeAmount, ct) ?? 0;
        return payments.Select(p => new PaymentMethodRow(p.Method, p.Count, p.Method == PaymentMethod.Cash ? p.Amount - change : p.Amount))
            .OrderByDescending(p => p.Amount).ToList();
    }

    private async Task<List<CashSessionRow>> ToRowsAsync(IReadOnlyList<CashSession> sessions, CancellationToken ct)
    {
        var terminalIds = sessions.Select(s => s.TerminalId).Distinct().ToList();
        var terminals = await db.Terminals.AsNoTracking().Where(t => terminalIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct);
        var userIds = sessions.SelectMany(s => new[] { s.OpenedByUserId, s.ClosedByUserId ?? s.OpenedByUserId }).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        decimal Sum(CashSession s, CashMovementType t) => s.Movements.Where(m => m.Type == t).Sum(m => m.Amount);
        return sessions.Select(s => new CashSessionRow(s.Id, terminals.GetValueOrDefault(s.TerminalId, "?"), users.GetValueOrDefault(s.OpenedByUserId, "?"),
            s.OpenedAt, s.ClosedByUserId is { } c ? users.GetValueOrDefault(c, "?") : null, s.ClosedAt,
            Sum(s, CashMovementType.Opening), Sum(s, CashMovementType.Supply), Sum(s, CashMovementType.Withdrawal),
            Sum(s, CashMovementType.CashSale), Sum(s, CashMovementType.CashRefund),
            s.ExpectedAmount ?? s.ExpectedCash, s.CountedAmount, s.Difference, s.Status)).ToList();
    }

    private async Task RequireAsync(Guid userId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(userId, Permissions.ViewReports, ct))
            throw new DomainException("Usuário sem permissão para ver relatórios.");
    }

    private static DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone).DateTime);
}
