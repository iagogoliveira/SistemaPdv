using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;

namespace Pdv.Domain.Purchases;

public enum PurchaseStatus
{
    Draft,
    Confirmed,
}

/// <summary>
/// Entrada de mercadorias (compra). Fica em rascunho enquanto os itens são conferidos; ao confirmar,
/// gera os movimentos de estoque e atualiza o custo dos produtos — uma única vez.
/// </summary>
public class Purchase : Entity
{
    private readonly List<PurchaseItem> _items = [];

    private Purchase() { }

    public Purchase(Guid supplierId, string? invoiceNumber, string? invoiceSeries, string? accessKey, DateTimeOffset? issuedAt,
        Guid createdByUserId, DateTimeOffset createdAt)
    {
        SupplierId = supplierId;
        InvoiceNumber = string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber.Trim();
        InvoiceSeries = string.IsNullOrWhiteSpace(invoiceSeries) ? null : invoiceSeries.Trim();
        if (!string.IsNullOrWhiteSpace(accessKey))
        {
            if (!Fiscal.AccessKey.IsValid(accessKey.Trim())) throw new DomainException("Chave de acesso da NF-e inválida.");
            AccessKey = accessKey.Trim();
        }
        IssuedAt = issuedAt;
        CreatedByUserId = createdByUserId;
        CreatedAt = createdAt;
        Status = PurchaseStatus.Draft;
    }

    public Guid SupplierId { get; private set; }
    public string? InvoiceNumber { get; private set; }
    public string? InvoiceSeries { get; private set; }

    /// <summary>Chave da NF-e do fornecedor: impede lançar a mesma nota duas vezes.</summary>
    public string? AccessKey { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }
    public PurchaseStatus Status { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? ConfirmedByUserId { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public IReadOnlyCollection<PurchaseItem> Items => _items;
    public decimal Total => _items.Sum(i => i.Total);

    public PurchaseItem AddItem(Guid productId, decimal quantity, decimal unitCost, string? supplierCode = null, string? supplierDescription = null)
    {
        EnsureDraft();
        var item = new PurchaseItem(Id, _items.Count + 1, productId, quantity, unitCost, supplierCode, supplierDescription);
        _items.Add(item);
        return item;
    }

    public void RemoveItem(int lineNumber)
    {
        EnsureDraft();
        var item = _items.SingleOrDefault(i => i.LineNumber == lineNumber) ?? throw new DomainException("Item não encontrado.");
        _items.Remove(item);
    }

    public void Confirm(Guid userId, DateTimeOffset at)
    {
        EnsureDraft();
        if (_items.Count == 0) throw new DomainException("A entrada não possui itens.");
        Status = PurchaseStatus.Confirmed;
        ConfirmedByUserId = userId;
        ConfirmedAt = at;
    }

    private void EnsureDraft()
    {
        if (Status != PurchaseStatus.Draft) throw new DomainException("A entrada já foi confirmada.");
    }
}

public class PurchaseItem : Entity
{
    private PurchaseItem() { }

    internal PurchaseItem(Guid purchaseId, int lineNumber, Guid productId, decimal quantity, decimal unitCost,
        string? supplierCode, string? supplierDescription)
    {
        if (quantity <= 0) throw new DomainException("Quantidade da entrada deve ser positiva.");
        if (unitCost < 0) throw new DomainException("Custo não pode ser negativo.");
        PurchaseId = purchaseId;
        LineNumber = lineNumber;
        ProductId = productId;
        Quantity = Money.RoundQuantity(quantity);
        UnitCost = Math.Round(unitCost, 4, MidpointRounding.AwayFromZero);
        Total = Money.Round(Quantity * UnitCost);
        SupplierCode = supplierCode?.Trim();
        SupplierDescription = supplierDescription?.Trim();
    }

    public Guid PurchaseId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid ProductId { get; private set; }

    /// <summary>Na unidade de estoque do produto (já convertida da unidade do fornecedor).</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Custo por unidade de estoque, com 4 casas (comum em notas de fornecedor).</summary>
    public decimal UnitCost { get; private set; }

    public decimal Total { get; private set; }
    public string? SupplierCode { get; private set; }
    public string? SupplierDescription { get; private set; }
}

/// <summary>
/// Vínculo "código do produto no fornecedor" → produto da loja, aprendido na primeira importação de XML.
/// O fator converte a unidade do fornecedor (ex.: caixa com 12) para a unidade de estoque.
/// </summary>
public class SupplierProduct : Entity
{
    private SupplierProduct() { SupplierCode = null!; }

    public SupplierProduct(Guid supplierId, string supplierCode, Guid productId, decimal conversionFactor)
    {
        if (string.IsNullOrWhiteSpace(supplierCode)) throw new DomainException("Código do fornecedor é obrigatório.");
        SupplierId = supplierId;
        SupplierCode = supplierCode.Trim();
        Relink(productId, conversionFactor);
    }

    public Guid SupplierId { get; private set; }
    public string SupplierCode { get; private set; }
    public Guid ProductId { get; private set; }
    public decimal ConversionFactor { get; private set; }

    public void Relink(Guid productId, decimal conversionFactor)
    {
        if (conversionFactor <= 0) throw new DomainException("Fator de conversão deve ser positivo.");
        ProductId = productId;
        ConversionFactor = Money.RoundQuantity(conversionFactor);
    }
}
