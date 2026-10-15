using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pdv.Application.Reports;
using Pdv.Application.Sales;

namespace Pdv.Desktop.Services;

/// <summary>Vale-troca e relatório de fechamento de caixa em bobina térmica (mesmo padrão do comprovante).</summary>
public sealed class TicketPrinter(ReceiptPrinter receiptPrinter)
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    public void PrintVoucher(ReturnReceipt r, string companyName) => Print(BuildVoucher(r, companyName), $"Vale-troca {r.VoucherCode}");

    public void PrintCashClosing(CashClosingReport report, string companyName) =>
        Print(BuildCashClosing(report, companyName), $"Fechamento de caixa {report.Session.Terminal}");

    public FlowDocument BuildVoucher(ReturnReceipt r, string companyName)
    {
        var t = new Ticket(receiptPrinter.Options.PaperWidthMm);
        t.Add(companyName, bold: true, center: true);
        t.Line();
        t.Add("VALE-TROCA", bold: true, center: true);
        t.Add($"Devolução da venda nº {r.SaleNumber}", center: true);
        t.Line();
        foreach (var (description, quantity, amount) in r.Items)
            t.Pair($"{quantity.ToString("0.###", PtBr)} x {description}", amount.ToString("N2", PtBr));
        t.Line();
        t.Pair("VALOR DO VALE R$", r.Total.ToString("N2", PtBr), bold: true);
        t.Add("");
        t.Add("CÓDIGO", center: true);
        t.Add($"{r.VoucherCode?[..4]}-{r.VoucherCode?[4..]}", bold: true, center: true, size: 1.8);
        t.Add("");
        t.Add($"Válido até {r.VoucherExpiresAt?.ToLocalTime():dd/MM/yyyy}", center: true);
        t.Add("Apresente este vale no caixa.", center: true);
        t.Add("Pode ser usado em partes até zerar o saldo.", center: true);
        return t.Document;
    }

    public FlowDocument BuildCashClosing(CashClosingReport report, string companyName)
    {
        var s = report.Session;
        var t = new Ticket(receiptPrinter.Options.PaperWidthMm);
        t.Add(companyName, bold: true, center: true);
        t.Line();
        t.Add("FECHAMENTO DE CAIXA", bold: true, center: true);
        t.Pair($"Caixa {s.Terminal}", s.Status == Domain.Cash.CashSessionStatus.Closed ? "FECHADO" : "ABERTO");
        t.Add($"Abertura: {s.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm} ({s.OpenedBy})");
        if (s.ClosedAt is { } closedAt) t.Add($"Fechamento: {closedAt.ToLocalTime():dd/MM/yyyy HH:mm} ({s.ClosedBy})");
        t.Line();
        t.Add("VENDAS", bold: true);
        t.Pair("Vendas concluídas", report.SalesCount.ToString(PtBr));
        t.Pair("Vendas canceladas", report.CancelledCount.ToString(PtBr));
        foreach (var m in report.SalesByMethod) t.Pair($"  {m.Label}", m.Amount.ToString("N2", PtBr));
        if (report.ReturnsInVoucher > 0) t.Pair("Devoluções em vale-troca", report.ReturnsInVoucher.ToString("N2", PtBr));
        t.Line();
        t.Add("DINHEIRO NA GAVETA", bold: true);
        t.Pair("Fundo de troco", s.Opening.ToString("N2", PtBr));
        t.Pair("(+) Vendas em dinheiro", s.CashSales.ToString("N2", PtBr));
        t.Pair("(+) Suprimentos", s.Supplies.ToString("N2", PtBr));
        t.Pair("(-) Sangrias", s.Withdrawals.ToString("N2", PtBr));
        t.Pair("(-) Devoluções/estornos", s.CashRefunds.ToString("N2", PtBr));
        t.Pair("ESPERADO", s.Expected.ToString("N2", PtBr), bold: true);
        if (s.Counted is { } counted)
        {
            t.Pair("CONTADO", counted.ToString("N2", PtBr), bold: true);
            var diff = s.Difference ?? 0;
            t.Pair(diff == 0 ? "DIFERENÇA" : diff < 0 ? "FALTA" : "SOBRA", Math.Abs(diff).ToString("N2", PtBr), bold: true);
        }
        t.Line();
        t.Add("MOVIMENTOS", bold: true);
        foreach (var (at, type, amount, reason, user) in report.Movements)
        {
            t.Pair($"{at.ToLocalTime():HH:mm} {type}", amount.ToString("N2", PtBr));
            if (!string.IsNullOrWhiteSpace(reason)) t.Add($"      {reason}");
            if (type != "Venda em dinheiro") t.Add($"      por {user}");
        }
        t.Line();
        t.Add("");
        t.Add("______________________________", center: true);
        t.Add("Assinatura do operador", center: true);
        t.Add("");
        t.Add("______________________________", center: true);
        t.Add("Assinatura do conferente", center: true);
        return t.Document;
    }

    private void Print(FlowDocument document, string jobName)
    {
        var dialog = new PrintDialog();
        if (!string.IsNullOrWhiteSpace(receiptPrinter.Options.Printer))
        {
            using var server = new LocalPrintServer();
            dialog.PrintQueue = new PrintQueue(server, receiptPrinter.Options.Printer);
        }
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, jobName);
    }

    /// <summary>Monta linhas de largura fixa (48/32 colunas) em fonte monoespaçada.</summary>
    private sealed class Ticket
    {
        private readonly int _columns;

        public Ticket(int paperWidthMm)
        {
            _columns = paperWidthMm >= 80 ? 48 : 32;
            var width = paperWidthMm / 25.4 * 96;
            const double padding = 6;
            Document = new FlowDocument
            {
                PageWidth = width,
                PageHeight = 4000,
                PagePadding = new Thickness(padding, 4, padding, 4),
                ColumnWidth = width,
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Floor((width - 2 * padding) / _columns / 0.55 * 10) / 10,
            };
        }

        public FlowDocument Document { get; }

        public void Add(string text, bool bold = false, bool center = false, double size = 1)
        {
            var columns = (int)(_columns / size);
            var content = center && text.Length < columns ? new string(' ', (columns - text.Length) / 2) + text : text;
            Document.Blocks.Add(new Paragraph(new Run(content))
            {
                Margin = new Thickness(0),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontSize = Document.FontSize * size,
            });
        }

        public void Pair(string left, string right, bool bold = false) => Add(ReceiptPrinter.Justify(left, right, _columns), bold);

        public void Line() => Add(new string('-', _columns));
    }
}
