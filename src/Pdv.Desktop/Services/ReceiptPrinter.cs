using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.Configuration;
using Pdv.Application.Sales;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;

namespace Pdv.Desktop.Services;

public enum ReceiptPrintMode
{
    Ask,
    Always,
    Never,
}

public sealed class ReceiptOptions
{
    public ReceiptPrintMode Mode { get; set; } = ReceiptPrintMode.Ask;

    /// <summary>Nome da impressora no Windows. Vazio = impressora padrão.</summary>
    public string? Printer { get; set; }

    /// <summary>Largura do papel em milímetros (80 ou 58 nas bobinas térmicas comuns).</summary>
    public int PaperWidthMm { get; set; } = 80;
}

/// <summary>
/// Comprovante NÃO fiscal impresso pelo driver do Windows (spooler). Funciona com qualquer impressora
/// térmica que tenha driver instalado; comandos ESC/POS diretos (guilhotina, gaveta) ficam para a matriz de hardware.
/// </summary>
public sealed class ReceiptPrinter(IConfiguration configuration)
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    public ReceiptOptions Options { get; } = configuration.GetSection("Pdv:Receipt").Get<ReceiptOptions>() ?? new();

    public void Print(ReceiptData receipt)
    {
        var dialog = new PrintDialog();
        if (!string.IsNullOrWhiteSpace(Options.Printer))
        {
            using var server = new LocalPrintServer();
            dialog.PrintQueue = new PrintQueue(server, Options.Printer);
        }

        var document = Build(receipt, Options.PaperWidthMm);
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, $"Comprovante {receipt.Number}");
    }

    public static FlowDocument Build(ReceiptData r, int paperWidthMm)
    {
        // Bobina de 80 mm ≈ 48 colunas; 58 mm ≈ 32 colunas (fonte monoespaçada).
        var columns = paperWidthMm >= 80 ? 48 : 32;
        var width = paperWidthMm / 25.4 * 96;
        var padding = 6.0;
        var doc = new FlowDocument
        {
            PageWidth = width,
            PageHeight = 4000, // bobina contínua; o driver corta no fim do conteúdo
            PagePadding = new Thickness(padding, 4, padding, 4),
            ColumnWidth = width,
            FontFamily = new FontFamily("Consolas"),
            // Largura de um caractere Consolas ≈ 0,55 em: ajusta a fonte para caber exatamente nas colunas.
            FontSize = Math.Floor((width - 2 * padding) / columns / 0.55 * 10) / 10,
            TextAlignment = TextAlignment.Left,
        };

        void Add(string text, bool bold = false, bool center = false) =>
            doc.Blocks.Add(new Paragraph(new Run(center ? Center(text, columns) : text))
            {
                Margin = new Thickness(0),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            });
        void Pair(string left, string right, bool bold = false) => Add(Justify(left, right, columns), bold);
        void Line() => Add(new string('-', columns));

        Add(r.CompanyName, bold: true, center: true);
        if (!string.IsNullOrEmpty(r.Cnpj)) Add($"CNPJ {FormatCnpj(r.Cnpj)}", center: true);
        Add(r.StoreName, center: true);
        Line();
        Add("COMPROVANTE NÃO FISCAL", bold: true, center: true);
        if (r.Status == SaleStatus.Cancelled) Add("*** VENDA CANCELADA ***", bold: true, center: true);
        Pair($"Venda nº {r.Number}", $"Caixa {r.TerminalCode}");
        Add(r.CompletedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", PtBr));
        Add($"Operador: {r.OperatorName}");
        if (r.CustomerDocument is { } customer)
            Add($"Consumidor: {(customer.Length == 11 ? "CPF" : "CNPJ")} {TaxDocuments.Format(customer)}");
        Line();

        foreach (var line in r.Lines)
        {
            foreach (var chunk in Wrap($"{line.LineNumber:000} {line.Description}", columns)) Add(chunk);
            Pair($"    {line.Quantity.ToString("0.###", PtBr)} {line.Unit} x {line.UnitPrice.ToString("N2", PtBr)}",
                (line.Total + line.Discount).ToString("N2", PtBr));
            if (line.Discount > 0) Pair("    desconto", "-" + line.Discount.ToString("N2", PtBr));
        }
        Line();

        if (r.DiscountTotal > 0)
        {
            Pair("Subtotal", r.Subtotal.ToString("N2", PtBr));
            Pair("Descontos", "-" + r.DiscountTotal.ToString("N2", PtBr));
        }
        Pair("TOTAL R$", r.Total.ToString("N2", PtBr), bold: true);
        foreach (var p in r.Payments) Pair(p.Label, p.Amount.ToString("N2", PtBr));
        if (r.Change > 0) Pair("Troco", r.Change.ToString("N2", PtBr), bold: true);
        Line();
        Add("Obrigado pela preferência!", center: true);
        Add("Este documento não tem valor fiscal.", center: true);
        return doc;
    }

    internal static string Justify(string left, string right, int columns)
    {
        var room = columns - right.Length - 1;
        if (left.Length > room) left = left[..Math.Max(0, room)];
        return left + new string(' ', columns - left.Length - right.Length) + right;
    }

    private static string Center(string text, int columns) =>
        text.Length >= columns ? text[..columns] : new string(' ', (columns - text.Length) / 2) + text;

    private static IEnumerable<string> Wrap(string text, int columns)
    {
        for (var i = 0; i < text.Length; i += columns)
            yield return text.Substring(i, Math.Min(columns, text.Length - i));
    }

    private static string FormatCnpj(string digits) =>
        digits.Length == 14 ? $"{digits[..2]}.{digits[2..5]}.{digits[5..8]}/{digits[8..12]}-{digits[12..]}" : digits;
}
