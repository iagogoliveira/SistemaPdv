using Pdv.Domain.Common;

namespace Pdv.Domain.Payments;

public enum PixChargeStatus
{
    /// <summary>QR Code gerado, aguardando o pagamento.</summary>
    Active,
    Paid,
    Expired,
    Cancelled,

    /// <summary>Pago e devolvido ao pagador (venda não concluída ou cancelada).</summary>
    Refunded,
}

/// <summary>
/// Cobrança Pix com QR Code dinâmico (API Pix do Banco Central, implementada pelos PSPs). Criada pelo servidor a pedido
/// do caixa; a confirmação vem do PSP. Uma cobrança paga só pode quitar uma venda.
/// </summary>
public class PixCharge : Entity
{
    private PixCharge() { Txid = Provider = CopyPaste = null!; }

    public PixCharge(Guid terminalId, Guid clientRequestId, decimal amount, string provider, string txid, string copyPaste,
        DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        if (Money.Round(amount) <= 0) throw new DomainException("Valor do Pix deve ser positivo.");
        if (!IsValidTxid(txid)) throw new DomainException("Identificador da cobrança Pix inválido.");
        if (string.IsNullOrWhiteSpace(copyPaste)) throw new DomainException("O PSP não devolveu o código do Pix.");
        TerminalId = terminalId;
        ClientRequestId = clientRequestId;
        Amount = Money.Round(amount);
        Provider = provider;
        Txid = txid;
        CopyPaste = copyPaste;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Status = PixChargeStatus.Active;
    }

    public Guid TerminalId { get; private set; }

    /// <summary>Carrinho que gerou a cobrança (mesma chave de idempotência da venda).</summary>
    public Guid ClientRequestId { get; private set; }

    public decimal Amount { get; private set; }
    public string Provider { get; private set; }

    /// <summary>Identificador da cobrança no PSP (26 a 35 letras e números).</summary>
    public string Txid { get; private set; }

    /// <summary>Código "copia e cola" (BR Code) que vira o QR Code.</summary>
    public string CopyPaste { get; private set; }

    public PixChargeStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>Identificador do Pix recebido (E + 31 caracteres), usado para conciliação e devolução.</summary>
    public string? EndToEndId { get; private set; }

    public string? PayerName { get; private set; }

    /// <summary>Venda quitada por esta cobrança.</summary>
    public Guid? SaleId { get; private set; }

    public string? Message { get; private set; }

    public static string NewTxid() => Guid.NewGuid().ToString("N"); // 32 caracteres alfanuméricos

    public static bool IsValidTxid(string? txid) =>
        txid is { Length: >= 26 and <= 35 } && txid.All(char.IsAsciiLetterOrDigit);

    public void MarkPaid(string endToEndId, decimal paidAmount, string? payerName, DateTimeOffset at)
    {
        if (Status == PixChargeStatus.Paid) return;
        if (Status is PixChargeStatus.Refunded) throw new DomainException("Cobrança Pix já devolvida.");
        if (Money.Round(paidAmount) != Amount)
            throw new DomainException($"Valor pago ({paidAmount:N2}) diferente da cobrança ({Amount:N2}).");
        Status = PixChargeStatus.Paid;
        EndToEndId = endToEndId;
        PayerName = payerName;
        PaidAt = at;
        Message = null;
    }

    public void Expire(DateTimeOffset at)
    {
        if (Status == PixChargeStatus.Active && at >= ExpiresAt) Status = PixChargeStatus.Expired;
    }

    public void Cancel(string? reason)
    {
        if (Status == PixChargeStatus.Paid) throw new DomainException("O Pix já foi pago: use a devolução.");
        if (Status != PixChargeStatus.Active) return;
        Status = PixChargeStatus.Cancelled;
        Message = reason;
    }

    /// <summary>Quita a venda. Chamado na mesma transação da finalização.</summary>
    public void LinkToSale(Guid saleId, decimal amount)
    {
        if (Status != PixChargeStatus.Paid) throw new DomainException("O Pix desta venda ainda não foi confirmado.");
        if (SaleId is { } other && other != saleId) throw new DomainException("Este Pix já quitou outra venda.");
        if (Money.Round(amount) != Amount) throw new DomainException("Valor do pagamento diferente do Pix recebido.");
        SaleId = saleId;
    }

    public void MarkRefunded(string? message)
    {
        if (Status != PixChargeStatus.Paid) throw new DomainException("Só um Pix pago pode ser devolvido.");
        Status = PixChargeStatus.Refunded;
        Message = message;
    }

    public void SetMessage(string? message) => Message = message;
}
