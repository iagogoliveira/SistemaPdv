using Pdv.Domain.Common;

namespace Pdv.Domain.Inventory;

public enum StockMovementType
{
    PurchaseEntry,
    Sale,
    SaleCancellation,
    Adjustment,
}

/// <summary>
/// Movimento imutável de estoque. O saldo é a soma dos movimentos; correções são feitas
/// com novos lançamentos compensatórios, nunca alterando ou apagando os anteriores.
/// </summary>
public class StockMovement : Entity
{
    private StockMovement() { }

    private StockMovement(Guid productId, StockMovementType type, decimal quantity, string? reason,
        Guid userId, DateTimeOffset occurredAt, Guid? referenceId)
    {
        if (quantity == 0) throw new DomainException("Quantidade movimentada não pode ser zero.");
        ProductId = productId;
        Type = type;
        Quantity = Money.RoundQuantity(quantity);
        Reason = reason?.Trim();
        UserId = userId;
        OccurredAt = occurredAt;
        ReferenceId = referenceId;
    }

    public Guid ProductId { get; private set; }
    public StockMovementType Type { get; private set; }

    /// <summary>Positivo para entrada, negativo para saída.</summary>
    public decimal Quantity { get; private set; }

    public string? Reason { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Origem rastreável: venda, compra etc.</summary>
    public Guid? ReferenceId { get; private set; }

    public static StockMovement ForSale(Guid productId, decimal quantitySold, Guid saleId, Guid userId, DateTimeOffset at)
    {
        if (quantitySold <= 0) throw new DomainException("Quantidade vendida deve ser positiva.");
        return new(productId, StockMovementType.Sale, -quantitySold, null, userId, at, saleId);
    }

    public static StockMovement ForSaleCancellation(Guid productId, decimal quantitySold, Guid saleId, Guid userId, DateTimeOffset at) =>
        new(productId, StockMovementType.SaleCancellation, quantitySold, null, userId, at, saleId);

    public static StockMovement ForPurchase(Guid productId, decimal quantity, Guid? purchaseId, Guid userId, DateTimeOffset at, string? note = null)
    {
        if (quantity <= 0) throw new DomainException("Quantidade de entrada deve ser positiva.");
        return new(productId, StockMovementType.PurchaseEntry, quantity, note, userId, at, purchaseId);
    }

    public static StockMovement ForAdjustment(Guid productId, decimal delta, string reason, Guid userId, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Ajuste de estoque exige motivo.");
        return new(productId, StockMovementType.Adjustment, delta, reason, userId, at, null);
    }
}
