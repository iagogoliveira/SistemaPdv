using System.ComponentModel;
using System.Printing;
using System.Runtime.InteropServices;
using Pdv.Client;
using Serilog;

namespace Pdv.Desktop.Services;

/// <summary>
/// Envia bytes direto para a impressora (tipo de dados RAW do spooler do Windows), sem passar pelo driver gráfico:
/// usado para abrir a gaveta e cortar o papel nas impressoras térmicas.
/// </summary>
public sealed class RawPrinter(ReceiptPrinter receipt)
{
    private ReceiptOptions Options => receipt.Options;

    /// <summary>Abre a gaveta, se configurada (<c>Pdv:Receipt:OpenDrawer</c>). Falha só gera aviso no log.</summary>
    public void OpenDrawer()
    {
        if (Options.OpenDrawer) TrySend([.. EscPos.Initialize(), .. EscPos.OpenDrawer(Options.DrawerPin)], "abrir gaveta");
    }

    /// <summary>Corta o papel depois do comprovante, se configurado (<c>Pdv:Receipt:CutPaper</c>).</summary>
    public void Cut()
    {
        if (Options.CutPaper) TrySend(EscPos.Cut(), "cortar papel");
    }

    private void TrySend(byte[] data, string action)
    {
        string? printer = null;
        try
        {
            printer = string.IsNullOrWhiteSpace(Options.Printer) ? LocalPrintServer.GetDefaultPrintQueue().FullName : Options.Printer;
            Send(printer, data);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PrintQueueException)
        {
            Log.Warning(ex, "Não foi possível {Action} na impressora {Printer}", action, printer);
        }
    }

    public static void Send(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var doc = new DocInfo { DocName = "Sistema PDV (ESC/POS)", DataType = "RAW" };
            if (!StartDocPrinter(handle, 1, doc)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!StartPagePrinter(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var buffer = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data, 0, buffer, data.Length);
                    if (!WritePrinter(handle, buffer, data.Length, out var written) || written != data.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr handle, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DocInfo doc);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);
}
