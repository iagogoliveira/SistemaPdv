using System.Security.Cryptography;
using System.Text;

namespace Pdv.Application.Security;

/// <summary>
/// Segredos em arquivos de configuração cifrados com DPAPI no escopo da máquina: qualquer usuário do Windows
/// neste computador (inclusive o serviço) lê; o arquivo copiado para outro computador não revela nada.
/// </summary>
public static class Dpapi
{
    public const string Prefix = "dpapi:";

    public static string? Protect(string? value, string purpose)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI exige Windows.");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(purpose), DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(data);
    }

    /// <summary>Aceita também o formato antigo (base64 sem prefixo). Devolve nulo se o segredo for de outro computador.</summary>
    public static string? Unprotect(string? value, string purpose)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI exige Windows.");
        var payload = value.StartsWith(Prefix, StringComparison.Ordinal) ? value[Prefix.Length..] : value;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(payload), Encoding.UTF8.GetBytes(purpose), DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // arquivo de outro computador: o segredo precisa ser informado de novo
        }
    }

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
