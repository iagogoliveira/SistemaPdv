namespace Pdv.Domain.Common;

public abstract class Entity
{
    // UUID v7 é ordenável por tempo, o que reduz fragmentação de índice no SQL Server.
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
}

public sealed class DomainException(string message) : Exception(message);

public static class Money
{
    /// <summary>Regra única de arredondamento monetário: 2 casas, meio para cima (padrão comercial).</summary>
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundQuantity(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
