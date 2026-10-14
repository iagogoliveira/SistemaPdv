namespace Pdv.Domain.Fiscal;

/// <summary>
/// Validação de CPF e CNPJ. O CNPJ aceita o formato alfanumérico (IN RFB 2.229/2024, emitido desde julho de 2026):
/// 12 posições com letras ou números e 2 dígitos verificadores numéricos, calculados com o valor ASCII − 48.
/// </summary>
public static class TaxDocuments
{
    /// <summary>Remove pontuação e deixa letras em maiúsculas.</summary>
    public static string Normalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsValidCnpj(string? value)
    {
        var c = Normalize(value);
        if (c.Length != 14) return false;
        if (!c[..12].All(ch => char.IsAsciiDigit(ch) || char.IsAsciiLetterUpper(ch))) return false;
        if (!char.IsAsciiDigit(c[12]) || !char.IsAsciiDigit(c[13])) return false;
        if (c.All(ch => ch == c[0])) return false;
        return CnpjCheckDigit(c[..12]) == c[12] - '0' && CnpjCheckDigit(c[..13]) == c[13] - '0';
    }

    public static bool IsValidCpf(string? value)
    {
        var c = Normalize(value);
        if (c.Length != 11 || !c.All(char.IsAsciiDigit) || c.All(ch => ch == c[0])) return false;
        return CpfCheckDigit(c[..9]) == c[9] - '0' && CpfCheckDigit(c[..10]) == c[10] - '0';
    }

    public static bool IsValidCpfOrCnpj(string? value) => Normalize(value).Length == 11 ? IsValidCpf(value) : IsValidCnpj(value);

    public static string Format(string? value)
    {
        var c = Normalize(value);
        return c.Length switch
        {
            11 => $"{c[..3]}.{c[3..6]}.{c[6..9]}-{c[9..]}",
            14 => $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}",
            _ => c,
        };
    }

    /// <summary>Pesos 2 a 9 da direita para a esquerda, reiniciando; o valor de cada caractere é ASCII − 48.</summary>
    internal static int CnpjCheckDigit(string body)
    {
        var sum = 0;
        var weight = 2;
        for (var i = body.Length - 1; i >= 0; i--)
        {
            sum += (body[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        var rest = sum % 11;
        return rest < 2 ? 0 : 11 - rest;
    }

    private static int CpfCheckDigit(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum += (body[i] - '0') * (body.Length + 1 - i);
        var rest = sum % 11;
        return rest < 2 ? 0 : 11 - rest;
    }
}
