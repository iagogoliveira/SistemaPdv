using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using Pdv.Application.Fiscal;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;

namespace Pdv.Fiscal.Tests;

/// <summary>
/// Monta e assina NFC-e reais com a DFe.NET usando um certificado de teste autoassinado (nada é enviado à SEFAZ).
/// </summary>
public sealed class ZeusNfceGatewayTests : IDisposable
{
    private const string Cnpj = "11222333000181";
    private readonly string _pfxPath = Path.Combine(Path.GetTempPath(), $"pdv-test-cert-{Guid.NewGuid():N}.pfx");
    private readonly NfceSettings _settings;
    private readonly ZeusNfceGateway _gateway = new();

    public ZeusNfceGatewayTests()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=EMPRESA TESTE LTDA:{Cnpj}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pfx, "teste123"));
        _settings = new NfceSettings(true, FiscalEnvironment.Homologation, 1, _pfxPath, "teste123", "000001", "CSCDETESTE0123456789");
    }

    public void Dispose() => File.Delete(_pfxPath);

    private static NfceDraft Draft(FiscalEmissionType type = FiscalEmissionType.Normal, string? csosn = "102", string? cst = null,
        string? customer = null, IReadOnlyList<NfcePayment>? payments = null)
    {
        var issuedAt = new DateTimeOffset(2026, 10, 14, 15, 30, 0, TimeSpan.FromHours(-3));
        var key = AccessKey.Build("SP", issuedAt, Cnpj, 65, 1, 123, type, 12345678);
        var emitter = new NfceEmitter(Cnpj, "Empresa Teste Ltda", "Loja Teste", "123456789", csosn is null ? TaxRegime.RegimeNormal : TaxRegime.SimplesNacional,
            "Praça da Sé", "100", null, "Sé", "3550308", "São Paulo", "SP", "01001000", "1133334444");
        NfceItem Item(int line, string description, decimal qty, decimal price, decimal discount, string? gtin) =>
            new(line, gtin ?? "INT-1", gtin, description, "48202000", null, "5102", "UN", qty, price, Money.Round(qty * price), discount,
                ProductOrigin.Nacional, csosn, cst, cst is null ? 0 : 18, cst is null ? "49" : "01", cst is null ? 0 : 1.65m,
                cst is null ? "49" : "01", cst is null ? 0 : 7.6m);
        return new NfceDraft(FiscalEnvironment.Homologation, type, key, 12345678, 1, 123, issuedAt,
            type == FiscalEmissionType.OfflineContingency ? "SEFAZ indisponível no momento da venda" : null,
            emitter, customer, null,
            [Item(1, "Caderno 96 folhas", 2, 14.90m, 0, "7891000315507"), Item(2, "Caneta azul", 1, 2.50m, 0.25m, null)],
            payments ?? [new NfcePayment(PaymentMethod.Pix, 10m), new NfcePayment(PaymentMethod.Cash, 30m)],
            payments is null ? 7.95m : 0, "Venda nº 1");
    }

    private static XmlDocument Load(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        return doc;
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", "http://www.portalfiscal.inf.br/nfe");
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
        return ns;
    }

    [Fact]
    public void Builds_signs_and_generates_qr_code()
    {
        var draft = Draft(customer: "52998224725");
        var signed = _gateway.BuildAndSign(draft, _settings);

        var doc = Load(signed.Xml);
        var ns = Ns(doc);
        var inf = (XmlElement)doc.SelectSingleNode("//n:infNFe", ns)!;
        Assert.Equal("NFe" + draft.AccessKey, inf.GetAttribute("Id"));
        Assert.Equal("65", doc.SelectSingleNode("//n:ide/n:mod", ns)!.InnerText);
        Assert.Equal("2", doc.SelectSingleNode("//n:ide/n:tpAmb", ns)!.InnerText);
        Assert.Equal("1", doc.SelectSingleNode("//n:ide/n:tpEmis", ns)!.InnerText);
        Assert.Equal("102", doc.SelectSingleNode("//n:det[@nItem='1']//n:CSOSN", ns)!.InnerText);

        // Regra da homologação: 1º item e destinatário com o aviso de "sem valor fiscal".
        Assert.Equal(ZeusNfceGateway.HomologationText, doc.SelectSingleNode("//n:det[@nItem='1']/n:prod/n:xProd", ns)!.InnerText);
        Assert.Equal(ZeusNfceGateway.HomologationCustomerText, doc.SelectSingleNode("//n:dest/n:xNome", ns)!.InnerText);
        Assert.Equal("52998224725", doc.SelectSingleNode("//n:dest/n:CPF", ns)!.InnerText);

        // GTIN válido vai no cEAN; código interno vai como "SEM GTIN".
        Assert.Equal("7891000315507", doc.SelectSingleNode("//n:det[@nItem='1']/n:prod/n:cEAN", ns)!.InnerText);
        Assert.Equal("SEM GTIN", doc.SelectSingleNode("//n:det[@nItem='2']/n:prod/n:cEAN", ns)!.InnerText);

        // Totais: 29,80 + 2,50 − 0,25 = 32,05; troco 7,95.
        Assert.Equal("32.30", doc.SelectSingleNode("//n:ICMSTot/n:vProd", ns)!.InnerText);
        Assert.Equal("0.25", doc.SelectSingleNode("//n:ICMSTot/n:vDesc", ns)!.InnerText);
        Assert.Equal("32.05", doc.SelectSingleNode("//n:ICMSTot/n:vNF", ns)!.InnerText);
        Assert.Equal("7.95", doc.SelectSingleNode("//n:pag/n:vTroco", ns)!.InnerText);
        Assert.Equal("20", doc.SelectSingleNode("//n:detPag[1]/n:tPag", ns)!.InnerText);   // Pix estático

        // Assinatura XMLDSig válida com o certificado.
        var signature = new SignedXml(doc);
        signature.LoadXml((XmlElement)doc.SelectSingleNode("//ds:Signature", ns)!);
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, "teste123");
        Assert.True(signature.CheckSignature(cert, verifySignatureOnly: true));

        // QR Code v3 e URL de consulta da SEFAZ-SP (homologação).
        Assert.Contains(draft.AccessKey, signed.QrCodeUrl, StringComparison.Ordinal);
        Assert.StartsWith("http", signed.QrCodeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("http", signed.ConsultationUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(signed.QrCodeUrl, doc.SelectSingleNode("//n:infNFeSupl/n:qrCode", ns)!.InnerText);
    }

    [Fact]
    public void Approximate_taxes_go_to_items_and_total()
    {
        var draft = Draft();
        draft = draft with { Items = [draft.Items[0] with { ApproximateTax = 9.39m }, draft.Items[1] with { ApproximateTax = 0.70m }] };
        var doc = Load(_gateway.BuildAndSign(draft, _settings).Xml);
        var ns = Ns(doc);
        Assert.Equal("9.39", doc.SelectSingleNode("//n:det[@nItem='1']/n:imposto/n:vTotTrib", ns)!.InnerText);
        Assert.Equal("10.09", doc.SelectSingleNode("//n:ICMSTot/n:vTotTrib", ns)!.InnerText);
    }

    [Fact]
    public void Invalid_xml_is_blocked_by_the_official_schema_before_sending()
    {
        var draft = Draft();
        draft = draft with { Items = [draft.Items[0] with { Ncm = "4820" }, draft.Items[1]] }; // NCM precisa de 8 dígitos
        var ex = Assert.Throws<DomainException>(() => _gateway.BuildAndSign(draft, _settings));
        Assert.Contains("esquema oficial", ex.Message);
        Assert.Contains("NCM", ex.Message);
    }

    [Fact]
    public void Integrated_card_and_dynamic_pix_fill_the_card_group()
    {
        var draft = Draft(payments:
        [
            new NfcePayment(PaymentMethod.CreditCard, 20m, Integrated: true, AcquirerCnpj: "11222333000181", CardBrand: "02", AuthorizationCode: "654321"),
            new NfcePayment(PaymentMethod.DebitCard, 5m, Integrated: true, AcquirerCnpj: "11222333000181", CardBrand: "06", AuthorizationCode: "111222"),
            new NfcePayment(PaymentMethod.Pix, 4.05m, Integrated: true, AcquirerCnpj: "99999999000191"),
            new NfcePayment(PaymentMethod.CreditCard, 3m),
        ]);
        var doc = Load(_gateway.BuildAndSign(draft, _settings).Xml);
        var ns = Ns(doc);
        string Value(int pag, string path) => doc.SelectSingleNode($"//n:detPag[{pag}]/{path}", ns)?.InnerText ?? "";

        // Crédito pelo TEF: tpIntegra 1, credenciadora, bandeira Mastercard e autorização.
        Assert.Equal("03", Value(1, "n:tPag"));
        Assert.Equal("1", Value(1, "n:card/n:tpIntegra"));
        Assert.Equal("11222333000181", Value(1, "n:card/n:CNPJ"));
        Assert.Equal("02", Value(1, "n:card/n:tBand"));
        Assert.Equal("654321", Value(1, "n:card/n:cAut"));
        // Débito Elo: bandeira sem nome na biblioteca sai como 99 (outras).
        Assert.Equal("04", Value(2, "n:tPag"));
        Assert.Equal("99", Value(2, "n:card/n:tBand"));
        // Pix confirmado pelo PSP: QR dinâmico (17), integrado, CNPJ do PSP, sem bandeira.
        Assert.Equal("17", Value(3, "n:tPag"));
        Assert.Equal("1", Value(3, "n:card/n:tpIntegra"));
        Assert.Equal("99999999000191", Value(3, "n:card/n:CNPJ"));
        Assert.Equal("", Value(3, "n:card/n:tBand"));
        // Maquininha não integrada: só tpIntegra 2.
        Assert.Equal("2", Value(4, "n:card/n:tpIntegra"));
        Assert.Equal("", Value(4, "n:card/n:cAut"));
    }

    [Fact]
    public void Offline_contingency_has_type_9_and_justification()
    {
        var draft = Draft(FiscalEmissionType.OfflineContingency);
        var signed = _gateway.BuildAndSign(draft, _settings);

        var doc = Load(signed.Xml);
        var ns = Ns(doc);
        Assert.Equal("9", doc.SelectSingleNode("//n:ide/n:tpEmis", ns)!.InnerText);
        Assert.NotNull(doc.SelectSingleNode("//n:ide/n:dhCont", ns));
        Assert.Contains("indispon", doc.SelectSingleNode("//n:ide/n:xJust", ns)!.InnerText, StringComparison.Ordinal);
        Assert.Equal('9', draft.AccessKey[34]);
        Assert.Contains(draft.AccessKey, signed.QrCodeUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Regime_normal_with_cst_00_computes_icms_pis_cofins()
    {
        var signed = _gateway.BuildAndSign(Draft(csosn: null, cst: "00"), _settings);
        var doc = Load(signed.Xml);
        var ns = Ns(doc);

        // Item 1: base 29,80 × 18% = 5,36; item 2: base 2,25 × 18% = 0,41.
        Assert.Equal("5.36", doc.SelectSingleNode("//n:det[@nItem='1']//n:ICMS00/n:vICMS", ns)!.InnerText);
        Assert.Equal("5.77", doc.SelectSingleNode("//n:ICMSTot/n:vICMS", ns)!.InnerText);
        Assert.Equal("32.05", doc.SelectSingleNode("//n:ICMSTot/n:vBC", ns)!.InnerText);
        Assert.NotNull(doc.SelectSingleNode("//n:det[@nItem='1']//n:PISAliq", ns));
        Assert.Equal("CRT", doc.SelectSingleNode("//n:emit/n:CRT", ns)!.LocalName);
        Assert.Equal("3", doc.SelectSingleNode("//n:emit/n:CRT", ns)!.InnerText);
    }

    [Fact]
    public void Unsupported_tax_code_is_refused_before_sending() =>
        Assert.Throws<DomainException>(() => _gateway.BuildAndSign(Draft(csosn: "900"), _settings));

    [Fact]
    public async Task Simulator_signs_real_xml_but_refuses_production()
    {
        var simulator = new SimulatedNfceGateway(_gateway);
        var signed = simulator.BuildAndSign(Draft(), _settings);
        var result = await simulator.AuthorizeAsync(signed.Xml, _settings, "SP", TestContext.Current.CancellationToken);
        Assert.Equal(SefazOutcome.Authorized, result.Outcome);

        var production = _settings with { Environment = FiscalEnvironment.Production };
        Assert.Throws<DomainException>(() => simulator.BuildAndSign(Draft(), production));
        await Assert.ThrowsAsync<DomainException>(() => simulator.AuthorizeAsync(signed.Xml, production, "SP", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Wrong_certificate_password_is_reported() =>
        Assert.Throws<DomainException>(() => _gateway.BuildAndSign(Draft(), _settings with { CertificatePassword = "errada" }));
}
