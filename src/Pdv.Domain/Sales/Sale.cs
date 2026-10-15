using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;

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

    /// <summary>Vale-troca gerado por devolução (crédito da loja).</summary>
    ExchangeVoucher,
}

/// <summary>
/// Retrato fiscal do item no momento da venda: o XML da NFC-e é montado a partir dele, mesmo que o cadastro
/// do produto ou do perfil tributário mude depois (ou que a emissão ocorra mais tarde, em contingência).
/// </summary>
public sealed record ItemFiscalData(
    string? Ncm, string? Cest, ProductOrigin Origin, string Cfop, string? Csosn, string? IcmsCst, decimal IcmsRate,
    string PisCst, decimal PisRate, string CofinsCst, decimal CofinsRate)
{
    public static ItemFiscalData From(string? ncm, string? cest, ProductOrigin origin, TaxProfile profile) =>
        new(ncm, cest, origin, profile.Cfop, profile.Csosn, profile.IcmsCst, profile.IcmsRate,
            profile.PisCst, profile.PisRate, profile.CofinsCst, profile.CofinsRate);
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

    /// <summary>CPF ou CNPJ informado pelo consumidor ("CPF na nota"), sem pontuação.</summary>
    public string? CustomerDocument { get; private set; }

    public string? CustomerName { get; private set; }

    public IReadOnlyCollection<SaleItem> Items => _items;
    public IReadOnlyCollection<Payment> Payments => _payments;
    public decimal PaidTotal => _payments.Sum(p => p.Amount);

    public SaleItem AddItem(Guid productId, string description, string unit, decimal quantity, decimal unitPrice, decimal discount = 0,
        ItemFiscalData? fiscal = null, decimal? unitCost = null)
    {
        EnsureDraft();
        var item = new SaleItem(Id, _items.Count + 1, productId, description, unit, quantity, unitPrice, discount, fiscal, unitCost);
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

    public void SetCustomer(string? document, string? name)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(document))
        {
            CustomerDocument = null;
            CustomerName = null;
            return;
        }
        if (!TaxDocuments.IsValidCpfOrCnpj(document)) throw new DomainException("CPF/CNPJ do consumidor inválido.");
        CustomerDocument = TaxDocuments.Normalize(document);
        CustomerName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public void SetDiscountAuthorizer(Guid userId)
    {
        EnsureDraft();
        DiscountAuthorizedByUserId = userId;
    }

    public Payment AddPayment(PaymentMethod method, decimal amount, string? reference = null, PaymentIntegration? integration = null)
    {
        EnsureDraft();
        var payment = new Payment(Id, method, amount, reference, integration);
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
        decimal quantity, decimal unitPrice, decimal discount, ItemFiscalData? fiscal, decimal? unitCost)
    {
        UnitCost = unitCost;
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

        if (fiscal is not null)
        {
            Ncm = fiscal.Ncm;
            Cest = fiscal.Cest;
            Origin = fiscal.Origin;
            Cfop = fiscal.Cfop;
            Csosn = fiscal.Csosn;
            IcmsCst = fiscal.IcmsCst;
            IcmsRate = fiscal.IcmsRate;
            PisCst = fiscal.PisCst;
            PisRate = fiscal.PisRate;
            CofinsCst = fiscal.CofinsCst;
            CofinsRate = fiscal.CofinsRate;
        }
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

    /// <summary>Custo do produto no momento da venda (para margem nos relatórios).</summary>
    public decimal? UnitCost { get; private set; }

    // ---- Retrato fiscal (ver ItemFiscalData) ----
    public string? Ncm { get; private set; }
    public string? Cest { get; private set; }
    public ProductOrigin Origin { get; private set; }
    public string? Cfop { get; private set; }
    public string? Csosn { get; private set; }
    public string? IcmsCst { get; private set; }
    public decimal IcmsRate { get; private set; }
    public string? PisCst { get; private set; }
    public decimal PisRate { get; private set; }
    public string? CofinsCst { get; private set; }
    public decimal CofinsRate { get; private set; }

    /// <summary>O item tem tudo o que a NFC-e exige (NCM e tributação).</summary>
    public bool HasFiscalData => Ncm is not null && Cfop is not null;
}

public class Payment : Entity
{
    private Payment() { }

    internal Payment(Guid saleId, PaymentMethod method, decimal amount, string? reference = null, PaymentIntegration? integration = null)
    {
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (Money.Round(amount) <= 0) throw new DomainException("Valor do pagamento deve ser positivo.");
        if (integration is not null && method is not (PaymentMethod.CreditCard or PaymentMethod.DebitCard or PaymentMethod.Pix))
            throw new DomainException("Só cartão e Pix podem ser pagamentos integrados.");
        SaleId = saleId;
        Method = method;
        Amount = Money.Round(amount);
        if (integration is not null)
        {
            IsIntegrated = true;
            TransactionId = integration.TransactionId;
            AuthorizationCode = integration.AuthorizationCode;
            CardBrand = integration.CardBrand;
            AcquirerCnpj = integration.AcquirerCnpj;
            AcquirerName = integration.AcquirerName;
        }
    }

    public Guid SaleId { get; private set; }
    public PaymentMethod Method { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>Identificação do meio, ex.: código do vale-troca usado.</summary>
    public string? Reference { get; private set; }

    /// <summary>Pago pelo TEF ou pelo Pix com QR dinâmico (tpIntegra = 1 na NFC-e); falso = maquininha/Pix sem integração.</summary>
    public bool IsIntegrated { get; private set; }

    /// <summary>NSU do cartão ou EndToEndId do Pix.</summary>
    public string? TransactionId { get; private set; }

    public string? AuthorizationCode { get; private set; }

    /// <summary>Bandeira no código da NFC-e (01 Visa, 02 Mastercard, 03 Amex, 06 Elo, 99 outras...).</summary>
    public string? CardBrand { get; private set; }

    /// <summary>CNPJ da credenciadora (cartão) ou do PSP (Pix).</summary>
    public string? AcquirerCnpj { get; private set; }

    public string? AcquirerName { get; private set; }
}

/// <summary>Dados de um pagamento aprovado pelo TEF ou confirmado pelo PSP do Pix.</summary>
public sealed record PaymentIntegration(string TransactionId, string? AuthorizationCode, string? CardBrand, string? AcquirerCnpj,
    string? AcquirerName)
{
    public static string? NormalizeBrand(string? brand)
    {
        var digits = new string((brand ?? "").Where(char.IsDigit).ToArray());
        return digits.Length is 1 or 2 ? digits.PadLeft(2, '0') : null;
    }
}
