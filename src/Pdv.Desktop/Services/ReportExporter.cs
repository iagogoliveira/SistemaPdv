using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pdv.Desktop.Views;

namespace Pdv.Desktop.Services;

/// <summary>Exporta relatórios para CSV (Excel em português: ";" e vírgula decimal) e monta a versão impressa em A4.</summary>
public static class ReportExporter
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    public static void WriteCsv(string path, IReadOnlyList<GridColumn> columns, IEnumerable rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', columns.Select(c => Quote(c.Header))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(';', columns.Select(c => Quote(Format(row, c, forCsv: true)))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static FlowDocument BuildDocument(string title, string subtitle, IReadOnlyList<GridColumn> columns, IEnumerable rows, string? summary)
    {
        var doc = new FlowDocument
        {
            PageWidth = 793.7, PageHeight = 1122.5, // A4 a 96 dpi
            PagePadding = new Thickness(48), ColumnWidth = 793.7,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
        };
        doc.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0) });
        doc.Blocks.Add(new Paragraph(new Run(subtitle)) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 12) });

        var table = new Table { CellSpacing = 0 };
        foreach (var _ in columns) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        var header = new TableRow { Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFF)) };
        foreach (var c in columns) header.Cells.Add(Cell(c.Header, bold: true, right: IsNumeric(c)));
        group.Rows.Add(header);
        foreach (var row in rows)
        {
            var tr = new TableRow();
            foreach (var c in columns) tr.Cells.Add(Cell(Format(row, c, forCsv: false), bold: false, right: IsNumeric(c)));
            group.Rows.Add(tr);
        }
        table.RowGroups.Add(group);
        doc.Blocks.Add(table);
        if (!string.IsNullOrWhiteSpace(summary))
            doc.Blocks.Add(new Paragraph(new Run(summary)) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        doc.Blocks.Add(new Paragraph(new Run($"Emitido em {DateTime.Now:dd/MM/yyyy HH:mm} · Sistema PDV"))
        { Foreground = Brushes.Gray, FontSize = 9, Margin = new Thickness(0, 16, 0, 0) });
        return doc;
    }

    public static void Print(FlowDocument document, string jobName)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        document.PageWidth = dialog.PrintableAreaWidth;
        document.ColumnWidth = dialog.PrintableAreaWidth;
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, jobName);
    }

    private static TableCell Cell(string text, bool bold, bool right) =>
        new(new Paragraph(new Run(text)) { Margin = new Thickness(0), TextAlignment = right ? TextAlignment.Right : TextAlignment.Left })
        {
            Padding = new Thickness(4, 3, 4, 3),
            BorderBrush = Brushes.Gainsboro,
            BorderThickness = new Thickness(0, 0, 0, 0.5),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };

    private static bool IsNumeric(GridColumn c) => c.Format is "C" or "N2" or "0.###" or "0.0" || c.Format?.StartsWith("{0:", StringComparison.Ordinal) == true;

    private static string Format(object row, GridColumn column, bool forCsv)
    {
        var value = row.GetType().GetProperty(column.Path)?.GetValue(row);
        if (value is null) return "";
        // No CSV, moeda vai como número puro (para o Excel somar).
        var format = forCsv && column.Format == "C" ? "N2" : column.Format;
        return value is IFormattable f && format is not null ? f.ToString(format, PtBr) : Convert.ToString(value, PtBr) ?? "";
    }

    private static string Quote(string value) =>
        value.Contains(';', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
