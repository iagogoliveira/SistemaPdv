using System.Globalization;
using System.Text;

namespace Pdv.Infrastructure.Payments;

/// <summary>
/// BR Code (padrão EMV® QRCPS-MPM do Pix): o texto "copia e cola" do QR Code. Usado pelo PSP simulado e nos testes;
/// em produção o código vem pronto do PSP.
/// </summary>
public static class BrCode
{
    /// <summary>QR dinâmico: aponta para a URL (location) da cobrança no PSP.</summary>
    public static string Dynamic(string location, string merchantName, string merchantCity, decimal? amount = null)
    {
        var account = Field("00", "br.gov.bcb.pix") + Field("25", location);
        var sb = new StringBuilder()
            .Append(Field("00", "01"))                       // versão do payload
            .Append(Field("01", "12"))                       // QR de uso único
            .Append(Field("26", account))
            .Append(Field("52", "0000"))                     // categoria do comerciante
            .Append(Field("53", "986"));                     // real
        if (amount is { } value) sb.Append(Field("54", value.ToString("0.00", CultureInfo.InvariantCulture)));
        sb.Append(Field("58", "BR"))
            .Append(Field("59", Ascii(merchantName, 25)))
            .Append(Field("60", Ascii(merchantCity, 15)))
            .Append(Field("62", Field("05", "***")))
            .Append("6304");
        var payload = sb.ToString();
        return payload + Crc16(payload).ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>Confere o CRC do código (últimos 4 caracteres).</summary>
    public static bool IsValid(string? code) =>
        code is { Length: > 8 } && code[^8..^4] == "6304"
        && string.Equals(code[^4..], Crc16(code[..^4]).ToString("X4", CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Field(string id, string value) => id + value.Length.ToString("00", CultureInfo.InvariantCulture) + value;

    /// <summary>CRC-16/CCITT-FALSE (polinômio 0x1021, inicial 0xFFFF), exigido pelo padrão.</summary>
    public static ushort Crc16(string text)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private static string Ascii(string value, int max)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var ascii = new string(normalized.Where(c => c < 128 && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToUpperInvariant().Trim();
        return ascii.Length <= max ? ascii : ascii[..max];
    }
}
