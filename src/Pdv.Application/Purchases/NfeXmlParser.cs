using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Pdv.Application.Fiscal;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;

namespace Pdv.Application.Purchases;

public sealed record ParsedInvoiceItem(
    int LineNumber, string SupplierCode, string? Gtin, string Description, string? Ncm, string? Cest, string Unit,
    decimal Quantity, decimal UnitPrice, decimal Total, decimal Discount)
{
    /// <summary>Valor efetivamente pago pelo item (produto − desconto).</summary>
    public decimal NetTotal => Total - Discount;
}

public sealed record ParsedInvoice(
    string AccessKey, string Number, string Series, DateTimeOffset? IssuedAt,
    string SupplierDocument, string SupplierName, string? RecipientDocument, decimal Total,
    IReadOnlyList<ParsedInvoiceItem> Items);

/// <summary>
/// Lê o XML da NF-e do fornecedor (arquivo nfeProc ou NFe, leiaute 4.00) para a entrada de mercadorias.
/// Só extrai o necessário para estoque e custo; não valida assinatura (a nota já foi autorizada pela SEFAZ).
/// </summary>
public static class NfeXmlParser
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/nfe";

    public static ParsedInvoice Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new DomainException("O arquivo não é um XML válido.");
        }

        var inf = doc.Descendants(Ns + "infNFe").FirstOrDefault()
            ?? throw new DomainException("O XML não é de uma NF-e (infNFe não encontrado).");
        var model = inf.Element(Ns + "ide")?.Element(Ns + "mod")?.Value;
        if (model is not "55") throw new DomainException("O XML não é de uma NF-e de fornecedor (modelo 55).");

        var key = (inf.Attribute("Id")?.Value ?? "").Replace("NFe", "", StringComparison.Ordinal);
        if (!AccessKey.IsValid(key)) throw new DomainException("Chave de acesso inválida no XML.");

        var ide = inf.Element(Ns + "ide")!;
        var emit = inf.Element(Ns + "emit") ?? throw new DomainException("Emitente não encontrado no XML.");
        var dest = inf.Element(Ns + "dest");

        var items = inf.Elements(Ns + "det").Select(det =>
        {
            var prod = det.Element(Ns + "prod") ?? throw new DomainException("Item sem dados de produto no XML.");
            string? Text(string name) => prod.Element(Ns + name)?.Value is { Length: > 0 } v ? v.Trim() : null;
            var ean = Text("cEAN") ?? Text("cEANTrib");
            return new ParsedInvoiceItem(
                int.Parse(det.Attribute("nItem")?.Value ?? "0", CultureInfo.InvariantCulture),
                Text("cProd") ?? "",
                Gtin.IsValid(ean) ? ean : null,
                Text("xProd") ?? "",
                Text("NCM") is { Length: 8 } ncm && FiscalCodes.IsValidNcm(ncm) ? ncm : null,
                Text("CEST"),
                (Text("uCom") ?? "UN").ToUpperInvariant(),
                Decimal(Text("qCom")),
                Decimal(Text("vUnCom")),
                Decimal(Text("vProd")),
                Decimal(Text("vDesc")));
        }).ToList();
        if (items.Count == 0) throw new DomainException("A NF-e não tem itens.");

        return new ParsedInvoice(
            key,
            ide.Element(Ns + "nNF")?.Value ?? "",
            ide.Element(Ns + "serie")?.Value ?? "",
            DateTimeOffset.TryParse(ide.Element(Ns + "dhEmi")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued) ? issued : null,
            TaxDocuments.Normalize(emit.Element(Ns + "CNPJ")?.Value ?? emit.Element(Ns + "CPF")?.Value),
            emit.Element(Ns + "xNome")?.Value ?? "",
            dest is null ? null : TaxDocuments.Normalize(dest.Element(Ns + "CNPJ")?.Value ?? dest.Element(Ns + "CPF")?.Value),
            Decimal(inf.Element(Ns + "total")?.Element(Ns + "ICMSTot")?.Element(Ns + "vNF")?.Value),
            items);
    }

    private static decimal Decimal(string? value) =>
        string.IsNullOrWhiteSpace(value) ? 0 : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
