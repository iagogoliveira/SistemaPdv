using Pdv.Domain.Common;
using Pdv.Domain.Sales;

namespace Pdv.Domain.Cash;

public enum CashSessionStatus
{
    Open,
    Closed,
}

public enum CashMovementType
{
    Opening,
    Supply,       // suprimento
    Withdrawal,   // sangria
    CashSale,
    CashRefund,
}

public class CashSession : Entity
{
    private readonly List<CashMovement> _movements = [];

    private CashSession() { }

    private CashSession(Guid terminalId, Guid userId, DateTimeOffset openedAt)
    {
        TerminalId = terminalId;
        OpenedByUserId = userId;
        OpenedAt = openedAt;
        Status = CashSessionStatus.Open;
    }

    public Guid TerminalId { get; private set; }
    public Guid OpenedByUserId { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public CashSessionStatus Status { get; private set; }
    public Guid? ClosedByUserId { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public decimal? ExpectedAmount { get; private set; }
    public decimal? CountedAmount { get; private set; }

    /// <summary>Contado − esperado. Negativo = falta; positivo = sobra.</summary>
    public decimal? Difference { get; private set; }

    /// <summary>
    /// Incrementado em toda operação (venda, movimento, fechamento). Força o UPDATE da linha da sessão,
    /// que é verificado pelo token de concorrência: uma venda e um fechamento simultâneos não podem ambos ser gravados.
    /// </summary>
    public int Revision { get; private set; }

    public int SaleCount { get; private set; }

    public IReadOnlyCollection<CashMovement> Movements => _movements;

    /// <summary>Saldo esperado em dinheiro. Requer que os movimentos estejam carregados.</summary>
    public decimal ExpectedCash => _movements.Sum(m => m.SignedAmount);

    public static CashSession Open(Guid terminalId, Guid userId, decimal openingAmount, DateTimeOffset at)
    {
        if (openingAmount < 0) throw new DomainException("Fundo de troco não pode ser negativo.");
        var session = new CashSession(terminalId, userId, at);
        session.AddMovement(CashMovementType.Opening, openingAmount, "Abertura", userId, at, null, allowZero: true);
        return session;
    }

    public CashMovement RegisterSupply(decimal amount, string? reason, Guid userId, DateTimeOffset at) =>
        AddMovement(CashMovementType.Supply, amount, reason, userId, at, null);

    public CashMovement RegisterWithdrawal(decimal amount, string reason, Guid userId, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Sangria exige motivo.");
        if (Money.Round(amount) > ExpectedCash) throw new DomainException("Sangria maior que o saldo em dinheiro do caixa.");
        return AddMovement(CashMovementType.Withdrawal, amount, reason, userId, at, null);
    }

    /// <summary>Vincula uma venda concluída à sessão. Não exige movimentos carregados.</summary>
    public void RegisterSale(Sale sale, Guid userId, DateTimeOffset at)
    {
        EnsureOpen();
        if (sale.Status != SaleStatus.Completed) throw new DomainException("Somente vendas concluídas entram no caixa.");
        if (sale.CashSessionId != Id) throw new DomainException("A venda pertence a outro caixa.");
        SaleCount++;
        Revision++;
        if (sale.NetCashReceived > 0)
            AddMovement(CashMovementType.CashSale, sale.NetCashReceived, null, userId, at, sale.Id);
    }

    public CashMovement RegisterCashRefund(decimal amount, Guid saleId, Guid userId, DateTimeOffset at) =>
        AddMovement(CashMovementType.CashRefund, amount, null, userId, at, saleId);

    public void Close(decimal countedAmount, Guid userId, DateTimeOffset at)
    {
        EnsureOpen();
        if (countedAmount < 0) throw new DomainException("Valor contado não pode ser negativo.");
        ExpectedAmount = ExpectedCash;
        CountedAmount = Money.Round(countedAmount);
        Difference = CountedAmount - ExpectedAmount;
        ClosedByUserId = userId;
        ClosedAt = at;
        Status = CashSessionStatus.Closed;
        Revision++;
    }

    public void EnsureOpen()
    {
        if (Status != CashSessionStatus.Open) throw new DomainException("O caixa está fechado.");
    }

    private CashMovement AddMovement(CashMovementType type, decimal amount, string? reason, Guid userId,
        DateTimeOffset at, Guid? referenceId, bool allowZero = false)
    {
        EnsureOpen();
        var rounded = Money.Round(amount);
        if (rounded < 0 || (!allowZero && rounded == 0)) throw new DomainException("Valor do movimento deve ser positivo.");
        var movement = new CashMovement(Id, type, rounded, reason, userId, at, referenceId);
        _movements.Add(movement);
        Revision++;
        return movement;
    }
}

public class CashMovement : Entity
{
    private CashMovement() { }

    internal CashMovement(Guid cashSessionId, CashMovementType type, decimal amount, string? reason,
        Guid userId, DateTimeOffset occurredAt, Guid? referenceId)
    {
        CashSessionId = cashSessionId;
        Type = type;
        Amount = amount;
        Reason = reason?.Trim();
        UserId = userId;
        OccurredAt = occurredAt;
        ReferenceId = referenceId;
    }

    public Guid CashSessionId { get; private set; }
    public CashMovementType Type { get; private set; }

    /// <summary>Sempre positivo; o sinal vem do tipo.</summary>
    public decimal Amount { get; private set; }

    public string? Reason { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? ReferenceId { get; private set; }

    public decimal SignedAmount => Type is CashMovementType.Withdrawal or CashMovementType.CashRefund ? -Amount : Amount;
}
