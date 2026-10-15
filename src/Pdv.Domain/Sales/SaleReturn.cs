using System.Security.Cryptography;
using Pdv.Domain.Common;

namespace Pdv.Domain.Sales;

public enum RefundMethod
{
    /// <summary>Devolve o dinheiro da gaveta.</summary>
    Cash,

    /// <summary>Gera um vale-troca para usar em outra compra.</summary>
    ExchangeVoucher,
}

/// <summary>
/// Devolução (total ou parcial) de itens de uma venda concluída. É operacional: devolve o estoque e
/// o valor (dinheiro ou vale-troca). A venda original não é alterada; a devolução é um lançamento próprio.
/// </summary>
public class SaleReturn : Entity
{
    private readonly List<SaleReturnItem> _items = [];

    private SaleReturn() { Reason = null!; }

    private SaleReturn(Guid saleId, RefundMethod method, string reason, Guid authorizedByUserId, Guid terminalId, DateTimeOffset at)
    {
        SaleId = saleId;
        RefundMethod = method;
        Reason = reason;
        AuthorizedByUserId = authorizedByUserId;
        TerminalId = terminalId;
        CreatedAt = at;
    }

    public Guid SaleId { get; private set; }
    public Guid TerminalId { get; private set; }
    public RefundMethod RefundMethod { get; private set; }
    public string Reason { get; private set; }
    public Guid AuthorizedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? ExchangeVoucherId { get; private set; }
    public IReadOnlyCollection<SaleReturnItem> Items => _items;
    public decimal Total => _items.Sum(i => i.RefundAmount);

    /// <param name="requested">Quantidade a devolver por número de linha da venda.</param>
    /// <param name="alreadyReturned">Quantidade e valor já devolvidos antes, por número de linha.</param>
    public static SaleReturn Create(Sale sale, IReadOnlyDictionary<int, decimal> requested,
        IReadOnlyDictionary<int, (decimal Quantity, decimal Amount)> alreadyReturned,
        RefundMethod method, string reason, Guid authorizedByUserId, Guid terminalId, DateTimeOffset at)
    {
        if (sale.Status != SaleStatus.Completed) throw new DomainException("Só é possível devolver itens de venda concluída.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Informe o motivo da devolução.");
        if (requested.Count == 0 || requested.Values.All(q => q <= 0)) throw new DomainException("Selecione ao menos um item para devolver.");

        var result = new SaleReturn(sale.Id, method, reason.Trim(), authorizedByUserId, terminalId, at);
        foreach (var (line, quantity) in requested.Where(r => r.Value > 0))
        {
            var item = sale.Items.SingleOrDefault(i => i.LineNumber == line) ?? throw new DomainException($"Item {line} não existe na venda.");
            var (doneQty, doneAmount) = alreadyReturned.TryGetValue(line, out var d) ? d : (0m, 0m);
            var available = item.Quantity - doneQty;
            if (quantity > available)
                throw new DomainException($"{item.Description}: só restam {available:0.###} {item.Unit} para devolver.");

            // Valor proporcional ao que foi pago (já com desconto); a última unidade leva o saldo exato, sem sobra de centavos.
            var amount = quantity == available
                ? item.Total - doneAmount
                : Money.Round(item.Total / item.Quantity * quantity);
            result._items.Add(new SaleReturnItem(result.Id, line, item.ProductId, item.Description, quantity, amount));
        }
        return result;
    }

    public void AttachVoucher(ExchangeVoucher voucher)
    {
        if (RefundMethod != RefundMethod.ExchangeVoucher) throw new DomainException("Esta devolução não gera vale-troca.");
        ExchangeVoucherId = voucher.Id;
    }
}

public class SaleReturnItem : Entity
{
    private SaleReturnItem() { Description = null!; }

    internal SaleReturnItem(Guid saleReturnId, int saleLineNumber, Guid productId, string description, decimal quantity, decimal refundAmount)
    {
        SaleReturnId = saleReturnId;
        SaleLineNumber = saleLineNumber;
        ProductId = productId;
        Description = description;
        Quantity = Money.RoundQuantity(quantity);
        RefundAmount = refundAmount;
    }

    public Guid SaleReturnId { get; private set; }
    public int SaleLineNumber { get; private set; }
    public Guid ProductId { get; private set; }
    public string Description { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal RefundAmount { get; private set; }
}

/// <summary>Vale-troca: crédito da loja gerado por uma devolução. Pode ser usado em partes até zerar.</summary>
public class ExchangeVoucher : Entity
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // sem 0/O e 1/I, para não confundir no balcão

    private ExchangeVoucher() { Code = null!; }

    public ExchangeVoucher(decimal amount, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        if (amount <= 0) throw new DomainException("Valor do vale-troca deve ser positivo.");
        Code = NewCode();
        Amount = Money.Round(amount);
        Balance = Amount;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public string Code { get; private set; }
    public decimal Amount { get; private set; }
    public decimal Balance { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public void Redeem(decimal amount, DateTimeOffset at)
    {
        if (at > ExpiresAt) throw new DomainException($"Vale-troca {Code} vencido em {ExpiresAt.ToLocalTime():dd/MM/yyyy}.");
        if (amount <= 0) throw new DomainException("Valor inválido para o vale-troca.");
        if (amount > Balance) throw new DomainException($"Vale-troca {Code} tem saldo de {Balance:C}.");
        Balance -= Money.Round(amount);
    }

    public static string NormalizeCode(string? code) => new((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NewCode() =>
        new(Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
}
