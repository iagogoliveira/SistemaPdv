using Pdv.Domain.Common;

namespace Pdv.Domain.Auditing;

/// <summary>Auditoria comercial (quem fez o quê), separada dos logs técnicos.</summary>
public class AuditEntry : Entity
{
    private AuditEntry() { Action = null!; }

    public AuditEntry(string action, Guid? userId, Guid? terminalId, Guid? referenceId, string? details, DateTimeOffset occurredAt)
    {
        Action = action;
        UserId = userId;
        TerminalId = terminalId;
        ReferenceId = referenceId;
        Details = details;
        OccurredAt = occurredAt;
    }

    public string Action { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? TerminalId { get; private set; }
    public Guid? ReferenceId { get; private set; }
    public string? Details { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}

public static class AuditActions
{
    public const string Login = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string AuthorizationFailed = "auth.authorization_failed";
    public const string SaleCompleted = "sale.completed";
    public const string SaleCancelled = "sale.cancelled";
    public const string DiscountAuthorized = "sale.discount_authorized";
    public const string CashOpened = "cash.opened";
    public const string CashSupply = "cash.supply";
    public const string CashWithdrawal = "cash.withdrawal";
    public const string CashClosed = "cash.closed";
    public const string StockAdjusted = "stock.adjusted";
}
