using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Configuration;
using Pdv.Application.Fiscal;
using Pdv.Application.Sales;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;
using QRCoder;

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

    /// <summary>Abre a gaveta ligada à impressora (ESC/POS) em vendas com dinheiro, suprimento e sangria.</summary>
    public bool OpenDrawer { get; set; }

    /// <summary>Conector da gaveta: 0 = pino 2 (padrão), 1 = pino 5.</summary>
    public int DrawerPin { get; set; }

    /// <summary>Envia o comando de corte (ESC/POS) após o comprovante — para drivers que não cortam sozinhos.</summary>
    public bool CutPaper { get; set; }
}

/// <summary>
/// Comprovante não fiscal ou DANFE NFC-e (quando a venda tem nota), impresso pelo driver do Windows (spooler). Funciona com qualquer impressora
/// térmica que tenha driver instalado; gaveta e guilhotina usam comandos ESC/POS diretos (<see cref="RawPrinter"/>).
/// </summary>
public sealed class ReceiptPrinter(IConfiguration configuration)
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    public ReceiptOptions Options { get; } = configuration.GetSection("Pdv:Receipt").Get<ReceiptOptions>() ?? new();

    public void Print(ReceiptData receipt, NfceInfo? fiscal = null)
    {
        var dialog = new PrintDialog();
        if (!string.IsNullOrWhiteSpace(Options.Printer))
        {
            using var server = new LocalPrintServer();
            dialog.PrintQueue = new PrintQueue(server, Options.Printer);
        }

        var document = Build(receipt, Options.PaperWidthMm, fiscal);
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator,
            fiscal is null ? $"Comprovante {receipt.Number}" : $"NFC-e {fiscal.Number}");
    }

    public static FlowDocument Build(ReceiptData r, int paperWidthMm, NfceInfo? fiscal = null)
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

        var isDanfe = fiscal is { Status: FiscalDocumentStatus.Authorized or FiscalDocumentStatus.Contingency };

        Add(r.CompanyName, bold: true, center: true);
        if (!string.IsNullOrEmpty(r.Cnpj)) Add($"CNPJ {TaxDocuments.Format(r.Cnpj)}", center: true);
        if (isDanfe && r.CompanyAddress is { } address)
            foreach (var chunk in Wrap(address, columns)) Add(chunk, center: true);
        else Add(r.StoreName, center: true);
        Line();
        if (isDanfe)
        {
            Add("DANFE NFC-e - Documento Auxiliar", bold: true, center: true);
            Add("da Nota Fiscal de Consumidor Eletrônica", bold: true, center: true);
            if (fiscal!.Environment == FiscalEnvironment.Homologation)
            {
                Add("EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO", bold: true, center: true);
                Add("SEM VALOR FISCAL", bold: true, center: true);
            }
            if (fiscal.Status == FiscalDocumentStatus.Contingency)
            {
                Add("EMITIDA EM CONTINGÊNCIA", bold: true, center: true);
                Add("Pendente de autorização", center: true);
            }
        }
        else Add("COMPROVANTE NÃO FISCAL", bold: true, center: true);
        if (r.Status == SaleStatus.Cancelled) Add("*** VENDA CANCELADA ***", bold: true, center: true);
        Pair($"Venda nº {r.Number}", $"Caixa {r.TerminalCode}");
        Add(r.CompletedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", PtBr));
        Add($"Operador: {r.OperatorName}");
        if (!isDanfe && r.CustomerDocument is { } customer)
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
        foreach (var p in r.Payments)
        {
            Pair(p.Label, p.Amount.ToString("N2", PtBr));
            if (p.Detail is { } detail) Add("  " + detail);
        }
        if (r.Change > 0) Pair("Troco", r.Change.ToString("N2", PtBr), bold: true);
        Line();
        // Lei 12.741/2012: valor aproximado dos tributos (tabela IBPT).
        if (r.ApproximateTaxes is { } taxes)
        {
            Add(taxes);
            Line();
        }

        if (isDanfe)
        {
            AddFiscalFooter(doc, r, fiscal!, columns, Add, Line);
            return doc;
        }
        Add("Obrigado pela preferência!", center: true);
        Add("Este documento não tem valor fiscal.", center: true);
        return doc;
    }

    /// <summary>Rodapé do DANFE NFC-e: consulta pela chave, consumidor, número/série, protocolo e QR Code.</summary>
    private static void AddFiscalFooter(FlowDocument doc, ReceiptData r, NfceInfo fiscal, int columns,
        Action<string, bool, bool> add, Action line)
    {
        add("Consulte pela Chave de Acesso em", false, true);
        foreach (var chunk in WrapAt(fiscal.ConsultationUrl ?? "", columns, '/')) add(chunk, false, true);
        var groups = AccessKey.Format(fiscal.AccessKey).Split(' ');
        add(string.Join(' ', groups[..6]), true, true);
        add(string.Join(' ', groups[6..]), true, true);
        line();
        add(r.CustomerDocument is { } c ? $"CONSUMIDOR {(c.Length == 11 ? "CPF" : "CNPJ")} {TaxDocuments.Format(c)}" : "CONSUMIDOR NÃO IDENTIFICADO",
            true, true);
        line();
        add($"NFC-e nº {fiscal.Number:000000000} Série {fiscal.Series:000}", true, true);
        add((fiscal.IssuedAt ?? r.CompletedAt).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", PtBr), false, true);
        if (fiscal.Status == FiscalDocumentStatus.Authorized)
        {
            add($"Protocolo de autorização: {fiscal.Protocol}", false, true);
            add($"Data de autorização: {fiscal.AuthorizedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", PtBr)}", false, true);
        }
        else
        {
            add("Via do consumidor - nota em contingência", false, true);
        }

        if (fiscal.QrCodeUrl is { } qr)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(qr, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            var image = new BitmapImage();
            using (var stream = new System.IO.MemoryStream(png))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            doc.Blocks.Add(new BlockUIContainer(new Image { Source = image, Width = 150, Height = 150, Margin = new Thickness(0, 8, 0, 8) }));
        }
    }

    internal static string Justify(string left, string right, int columns)
    {
        var room = columns - right.Length - 1;
        if (left.Length > room) left = left[..Math.Max(0, room)];
        return left + new string(' ', columns - left.Length - right.Length) + right;
    }

    private static string Center(string text, int columns) =>
        text.Length >= columns ? text[..columns] : new string(' ', (columns - text.Length) / 2) + text;

    /// <summary>Quebra preferindo o caractere indicado (ex.: "/" em URLs), para não cortar palavras.</summary>
    private static IEnumerable<string> WrapAt(string text, int columns, char breakAfter)
    {
        while (text.Length > columns)
        {
            var cut = text.LastIndexOf(breakAfter, columns - 1);
            cut = cut <= 0 ? columns : cut + 1;
            yield return text[..cut];
            text = text[cut..];
        }
        if (text.Length > 0) yield return text;
    }

    private static IEnumerable<string> Wrap(string text, int columns)
    {
        for (var i = 0; i < text.Length; i += columns)
            yield return text.Substring(i, Math.Min(columns, text.Length - i));
    }

}
