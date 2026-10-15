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

/// <summary>Configuração simples do sistema guardada no banco (chave/valor), ex.: início da demonstração.</summary>
public class AppSetting
{
    private AppSetting() { Key = Value = null!; }

    public AppSetting(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; private set; }
    public string Value { get; private set; }

    public void Set(string value) => Value = value;
}
