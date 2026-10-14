using Pdv.Domain.Common;

namespace Pdv.Domain.Sales;

public enum SaleStatus
{
    Draft,
    Completed,
    Cancelled,
}

/// <summary>Independente da situação da venda: uma venda concluída pode ter emissão fiscal pendente ou rejeitada.</summary>
public enum FiscalStatus
{
    NotIssued,
    Pending,
    Authorized,
    Rejected,
    Cancelled,
}

public enum PaymentMethod
{
    Cash,
    CreditCard,
    DebitCard,
    Pix,
    Other,
}

public class Sale : Entity
{
    private readonly List<SaleItem> _items = [];
    private readonly List<Payment> _payments = [];

    private Sale() { }

    public Sale(Guid clientRequestId, Guid terminalId, Guid cashSessionId, Guid operatorUserId, DateTimeOffset startedAt)
    {
        if (clientRequestId == Guid.Empty) throw new DomainException("Identificador da solicitação é obrigatório.");
        ClientRequestId = clientRequestId;
        TerminalId = terminalId;
        CashSessionId = cashSessionId;
        OperatorUserId = operatorUserId;
        StartedAt = startedAt;
        Status = SaleStatus.Draft;
        FiscalStatus = FiscalStatus.NotIssued;
    }

    /// <summary>Chave de idempotência gerada pelo terminal: repetir a solicitação não cria outra venda.</summary>
    public Guid ClientRequestId { get; private set; }

    /// <summary>Número sequencial legível (comprovantes), gerado pelo banco.</summary>
    public long Number { get; private set; }

    public Guid TerminalId { get; private set; }
    public Guid CashSessionId { get; private set; }
    public Guid OperatorUserId { get; private set; }
    public Guid? DiscountAuthorizedByUserId { get; private set; }
    public SaleStatus Status { get; private set; }
    public FiscalStatus FiscalStatus { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public string? CancellationReason { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal DiscountTotal { get; private set; }
    public decimal Total { get; private set; }
    public decimal ChangeAmount { get; private set; }

    public IReadOnlyCollection<SaleItem> Items => _items;
    public IReadOnlyCollection<Payment> Payments => _payments;
    public decimal PaidTotal => _payments.Sum(p => p.Amount);

    public SaleItem AddItem(Guid productId, string description, string unit, decimal quantity, decimal unitPrice, decimal discount = 0)
    {
        EnsureDraft();
        var item = new SaleItem(Id, _items.Count + 1, productId, description, unit, quantity, unitPrice, discount);
        _items.Add(item);
        Recalculate();
        return item;
    }

    public void RemoveItem(int lineNumber)
    {
        EnsureDraft();
        var item = _items.SingleOrDefault(i => i.LineNumber == lineNumber)
            ?? throw new DomainException($"Item {lineNumber} não encontrado.");
        _items.Remove(item);
        Recalculate();
    }

    public void SetDiscountAuthorizer(Guid userId)
    {
        EnsureDraft();
        DiscountAuthorizedByUserId = userId;
    }

    public Payment AddPayment(PaymentMethod method, decimal amount)
    {
        EnsureDraft();
        var payment = new Payment(Id, method, amount);
        _payments.Add(payment);
        return payment;
    }

    public void Complete(DateTimeOffset at)
    {
        EnsureDraft();
        if (_items.Count == 0) throw new DomainException("A venda não possui itens.");
        if (DiscountTotal > 0 && DiscountAuthorizedByUserId is null)
            throw new DomainException("Desconto exige autorização.");

        var paid = PaidTotal;
        if (paid < Total) throw new DomainException($"Pagamento insuficiente: faltam {Total - paid:C}.");

        // Troco só pode sair do dinheiro recebido; cartão/Pix não geram troco.
        var change = paid - Total;
        var cashPaid = _payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        if (change > cashPaid) throw new DomainException("Troco maior que o valor recebido em dinheiro.");

        ChangeAmount = change;
        CompletedAt = at;
        Status = SaleStatus.Completed;
    }

    /// <summary>Dinheiro que efetivamente entra na gaveta (recebido − troco).</summary>
    public decimal NetCashReceived =>
        _payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount) - ChangeAmount;

    public void Cancel(string reason, Guid userId, DateTimeOffset at)
    {
        if (Status != SaleStatus.Completed) throw new DomainException("Somente vendas concluídas podem ser canceladas.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Cancelamento exige motivo.");
        Status = SaleStatus.Cancelled;
        CancellationReason = reason.Trim();
        CancelledByUserId = userId;
        CancelledAt = at;
    }

    private void EnsureDraft()
    {
        if (Status != SaleStatus.Draft) throw new DomainException("A venda já foi finalizada.");
    }

    private void Recalculate()
    {
        Subtotal = _items.Sum(i => i.GrossTotal);
        DiscountTotal = _items.Sum(i => i.Discount);
        Total = Subtotal - DiscountTotal;
    }
}

/// <summary>Retrato do produto no momento da venda: alterações posteriores no cadastro não afetam a venda.</summary>
public class SaleItem : Entity
{
    private SaleItem() { Description = Unit = null!; }

    internal SaleItem(Guid saleId, int lineNumber, Guid productId, string description, string unit,
        decimal quantity, decimal unitPrice, decimal discount)
    {
        if (quantity <= 0) throw new DomainException("Quantidade deve ser positiva.");
        if (unitPrice < 0) throw new DomainException("Preço unitário não pode ser negativo.");
        SaleId = saleId;
        LineNumber = lineNumber;
        ProductId = productId;
        Description = description;
        Unit = unit;
        Quantity = Money.RoundQuantity(quantity);
        UnitPrice = Money.Round(unitPrice);
        GrossTotal = Money.Round(Quantity * UnitPrice);
        Discount = Money.Round(discount);
        if (Discount < 0 || Discount > GrossTotal) throw new DomainException("Desconto inválido para o item.");
        Total = GrossTotal - Discount;
    }

    public Guid SaleId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid ProductId { get; private set; }
    public string Description { get; private set; }
    public string Unit { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal GrossTotal { get; private set; }
    public decimal Discount { get; private set; }
    public decimal Total { get; private set; }
}

public class Payment : Entity
{
    private Payment() { }

    internal Payment(Guid saleId, PaymentMethod method, decimal amount)
    {
        if (Money.Round(amount) <= 0) throw new DomainException("Valor do pagamento deve ser positivo.");
        SaleId = saleId;
        Method = method;
        Amount = Money.Round(amount);
    }

    public Guid SaleId { get; private set; }
    public PaymentMethod Method { get; private set; }
    public decimal Amount { get; private set; }
}
