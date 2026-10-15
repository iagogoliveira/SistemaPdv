namespace Pdv.Client;

/// <summary>
/// Comandos ESC/POS das impressoras térmicas (Epson, Elgin, Bematech, Daruma, Tanca e compatíveis), enviados em modo
/// RAW: abertura da gaveta de dinheiro ligada à impressora e corte do papel.
/// </summary>
public static class EscPos
{
    /// <summary>ESC p m t1 t2 — pulso no conector da gaveta (pino 2 ou 5), 50 ms ligado / 500 ms desligado.</summary>
    public static byte[] OpenDrawer(int pin = 0) => [0x1B, 0x70, (byte)(pin == 1 ? 1 : 0), 0x19, 0xFA];

    /// <summary>GS V 66 n — avança o papel até a guilhotina e corta (corte parcial, o mais comum).</summary>
    public static byte[] Cut(int feedLines = 3) => [0x1D, 0x56, 0x42, (byte)Math.Clamp(feedLines, 0, 255)];

    /// <summary>ESC @ — reinicia a impressora antes dos comandos.</summary>
    public static byte[] Initialize() => [0x1B, 0x40];
}
