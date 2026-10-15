using Microsoft.EntityFrameworkCore;
using Pdv.Domain.Sales;

namespace Pdv.Application.Sales;

public sealed record SaleListItem(Guid Id, long Number, DateTimeOffset? CompletedAt, decimal Total, SaleStatus Status, string OperatorName);

public sealed record ReceiptLine(int LineNumber, string Description, decimal Quantity, string Unit, decimal UnitPrice, decimal Discount, decimal Total);

public sealed record ReceiptPayment(PaymentMethod Method, decimal Amount)
{
    public string Label => PaymentLabels.For(Method);
}

/// <summary>Dados do comprovante não fiscal, independentes da forma de impressão.</summary>
public sealed record ReceiptData(
    string CompanyName,
    string? Cnpj,
    string StoreName,
    string TerminalCode,
    long Number,
    DateTimeOffset CompletedAt,
    string OperatorName,
    IReadOnlyList<ReceiptLine> Lines,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal Total,
    IReadOnlyList<ReceiptPayment> Payments,
    decimal Change,
    SaleStatus Status,
    string? CustomerDocument = null,
    string? CompanyAddress = null);

public static class PaymentLabels
{
    public static string For(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Dinheiro",
        PaymentMethod.Pix => "Pix",
        PaymentMethod.DebitCard => "Cartão de débito",
        PaymentMethod.CreditCard => "Cartão de crédito",
        PaymentMethod.ExchangeVoucher => "Vale-troca",
        _ => "Outros",
    };
}

public sealed class SalesQueryService(IPdvDbContext db)
{
    public async Task<IReadOnlyList<SaleListItem>> ListRecentAsync(Guid terminalId, int take = 50, CancellationToken ct = default) =>
        await db.Sales.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status != SaleStatus.Draft)
            .OrderByDescending(s => s.Number)
            .Take(take)
            .Join(db.Users, s => s.OperatorUserId, u => u.Id,
                (s, u) => new SaleListItem(s.Id, s.Number, s.CompletedAt, s.Total, s.Status, u.DisplayName))
            .ToListAsync(ct);

    /// <summary>Venda gravada com esta chave de idempotência (para o caixa saber se uma venda interrompida chegou ao servidor).</summary>
    public async Task<SaleListItem?> FindByRequestAsync(Guid clientRequestId, CancellationToken ct = default) =>
        await db.Sales.AsNoTracking()
            .Where(s => s.ClientRequestId == clientRequestId && s.Status != SaleStatus.Draft)
            .Join(db.Users, s => s.OperatorUserId, u => u.Id,
                (s, u) => new SaleListItem(s.Id, s.Number, s.CompletedAt, s.Total, s.Status, u.DisplayName))
            .SingleOrDefaultAsync(ct);

    public async Task<ReceiptData?> GetReceiptAsync(Guid saleId, CancellationToken ct = default)
    {
        var sale = await db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments)
            .SingleOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null) return null;

        var header = await (
            // Sem rastreamento: o endereço é um tipo "owned" e não pode ser projetado sozinho numa consulta rastreada.
            from t in db.Terminals.AsNoTracking()
            join st in db.Stores.AsNoTracking() on t.StoreId equals st.Id
            join c in db.Companies.AsNoTracking() on st.CompanyId equals c.Id
            where t.Id == sale.TerminalId
            select new { c.LegalName, c.Cnpj, StoreName = st.Name, t.Code, c.Address }).SingleAsync(ct);
        var address = header.Address is { } a
            ? $"{a.Street}, {a.Number}{(a.Complement is null ? "" : " " + a.Complement)} - {a.District} - {a.City}/{a.Uf}"
            : null;
        var operatorName = await db.Users.Where(u => u.Id == sale.OperatorUserId).Select(u => u.DisplayName).SingleAsync(ct);

        return new ReceiptData(
            header.LegalName, header.Cnpj, header.StoreName, header.Code, sale.Number, sale.CompletedAt ?? sale.StartedAt, operatorName,
            sale.Items.OrderBy(i => i.LineNumber)
                .Select(i => new ReceiptLine(i.LineNumber, i.Description, i.Quantity, i.Unit, i.UnitPrice, i.Discount, i.Total)).ToList(),
            sale.Subtotal, sale.DiscountTotal, sale.Total,
            sale.Payments.Select(p => new ReceiptPayment(p.Method, p.Amount)).ToList(),
            sale.ChangeAmount, sale.Status, sale.CustomerDocument, address);
    }
}
