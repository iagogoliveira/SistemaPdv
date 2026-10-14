using Pdv.Domain.Catalog;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;
using Pdv.Domain.Stores;

namespace Pdv.Domain.Tests;

public class TaxDocumentTests
{
    [Theory]
    [InlineData("11.222.333/0001-81", true)]
    [InlineData("12.ABC.345/01DE-35", true)]   // exemplo oficial da Receita (CNPJ alfanumérico)
    [InlineData("12abc34501de35", true)]       // minúsculas e sem pontuação
    [InlineData("12.ABC.345/01DE-36", false)]
    [InlineData("11.222.333/0001-82", false)]
    [InlineData("00.000.000/0000-00", false)]
    [InlineData("12.ABC.345/01DE-3A", false)]  // verificadores são sempre numéricos
    [InlineData("1122233300018", false)]
    public void Cnpj_validation_supports_numeric_and_alphanumeric(string cnpj, bool valid) =>
        Assert.Equal(valid, TaxDocuments.IsValidCnpj(cnpj));

    [Theory]
    [InlineData("529.982.247-25", true)]
    [InlineData("52998224725", true)]
    [InlineData("529.982.247-24", false)]
    [InlineData("111.111.111-11", false)]
    public void Cpf_validation(string cpf, bool valid) => Assert.Equal(valid, TaxDocuments.IsValidCpf(cpf));

    [Fact]
    public void Format_applies_mask()
    {
        Assert.Equal("12.ABC.345/01DE-35", TaxDocuments.Format("12abc34501de35"));
        Assert.Equal("529.982.247-25", TaxDocuments.Format("52998224725"));
    }
}

public class TaxProfileTests
{
    [Fact]
    public void Profile_requires_exactly_one_icms_code()
    {
        Assert.Throws<DomainException>(() => new TaxProfile("X", "5102", null, null, 0, "49", 0, "49", 0));
        Assert.Throws<DomainException>(() => new TaxProfile("X", "5102", "102", "00", 0, "49", 0, "49", 0));
    }

    [Theory]
    [InlineData("6102")]   // interestadual: não cabe em NFC-e
    [InlineData("510")]
    public void Nfce_profile_needs_internal_cfop(string cfop) =>
        Assert.Throws<DomainException>(() => new TaxProfile("X", cfop, "102", null, 0, "49", 0, "49", 0));

    [Fact]
    public void Invalid_codes_and_rates_are_rejected()
    {
        Assert.Throws<DomainException>(() => new TaxProfile("X", "5102", "999", null, 0, "49", 0, "49", 0));
        Assert.Throws<DomainException>(() => new TaxProfile("X", "5102", null, "07", 0, "01", 0, "01", 0));
        Assert.Throws<DomainException>(() => new TaxProfile("X", "5102", null, "00", 120, "01", 0, "01", 0));
        Assert.Throws<DomainException>(() => new TaxProfile("X", "5102", "102", null, 0, "47", 0, "49", 0));
    }

    [Fact]
    public void Profile_must_match_company_regime()
    {
        var simples = new TaxProfile("Simples", "5102", "102", null, 0, "49", 0, "49", 0);
        var normal = new TaxProfile("Normal", "5102", null, "00", 18, "01", 1.65m, "01", 7.6m);

        simples.EnsureCompatibleWith(TaxRegime.SimplesNacional);
        simples.EnsureCompatibleWith(TaxRegime.SimplesNacionalMei);
        normal.EnsureCompatibleWith(TaxRegime.RegimeNormal);
        normal.EnsureCompatibleWith(TaxRegime.SimplesNacionalExcessoSublimite);
        Assert.Throws<DomainException>(() => simples.EnsureCompatibleWith(TaxRegime.RegimeNormal));
        Assert.Throws<DomainException>(() => normal.EnsureCompatibleWith(TaxRegime.SimplesNacional));
    }
}

public class CompanyAndProductFiscalTests
{
    private static Address SampleAddress() =>
        Address.Create("Rua das Flores", "100", null, "Centro", "São Paulo", "3550308", "sp", "01001-000");

    [Fact]
    public void Company_registration_validates_and_normalizes()
    {
        var company = new Company("Minha Empresa", null);
        Assert.False(company.IsFiscallyConfigured);

        Assert.Throws<DomainException>(() => company.UpdateRegistration("Loja X", null, "11.222.333/0001-82", "123456789", null,
            TaxRegime.SimplesNacional, SampleAddress(), null));

        company.UpdateRegistration("Loja X Ltda", "Loja X", "12.abc.345/01de-35", "123.456.789", null,
            TaxRegime.SimplesNacional, SampleAddress(), "(11) 3333-4444");
        Assert.Equal("12ABC34501DE35", company.Cnpj);
        Assert.Equal("123456789", company.StateRegistration);
        Assert.Equal("SP", company.Address!.Uf);
        Assert.Equal("01001000", company.Address.ZipCode);

        var profile = new TaxProfile("Simples", "5102", "102", null, 0, "49", 0, "49", 0);
        company.SetDefaultTaxProfile(profile);
        Assert.True(company.IsFiscallyConfigured);
    }

    [Theory]
    [InlineData("SX", "3550308", "01001000")]
    [InlineData("SP", "355030", "01001000")]
    [InlineData("SP", "3550308", "0100100")]
    public void Address_validation(string uf, string ibge, string cep) =>
        Assert.Throws<DomainException>(() => Address.Create("Rua", "1", null, "Centro", "Cidade", ibge, uf, cep));

    [Fact]
    public void Default_profile_must_match_regime()
    {
        var company = new Company("Empresa", null);
        company.UpdateRegistration("Empresa", null, "11222333000181", "ISENTO", null, TaxRegime.RegimeNormal, SampleAddress(), null);
        Assert.Throws<DomainException>(() => company.SetDefaultTaxProfile(new TaxProfile("S", "5102", "102", null, 0, "49", 0, "49", 0)));
    }

    [Theory]
    [InlineData("4820.20.00", "48202000")]
    [InlineData("48202000", "48202000")]
    public void Product_ncm_is_normalized(string ncm, string expected)
    {
        var product = new Product("Caderno", 10, 5);
        product.SetFiscalData(ncm, null, ProductOrigin.Nacional, null);
        Assert.Equal(expected, product.Ncm);
    }

    [Theory]
    [InlineData("4820200")]
    [InlineData("4820-20-00X")]
    public void Product_rejects_invalid_ncm(string ncm) =>
        Assert.Throws<DomainException>(() => new Product("Caderno", 10, 5).SetFiscalData(ncm, null, ProductOrigin.Nacional, null));
}

public class SaleFiscalSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Item_keeps_fiscal_snapshot_even_if_profile_changes_later()
    {
        var profile = new TaxProfile("Simples", "5102", "102", null, 0, "49", 0, "49", 0);
        var sale = new Sale(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        var item = sale.AddItem(Guid.NewGuid(), "Caderno", "UN", 1, 10m, fiscal: ItemFiscalData.From("48202000", null, ProductOrigin.Nacional, profile));

        profile.Update("Simples", "5405", "500", null, 0, "49", 0, "49", 0);

        Assert.True(item.HasFiscalData);
        Assert.Equal("5102", item.Cfop);
        Assert.Equal("102", item.Csosn);
        Assert.Equal("48202000", item.Ncm);
    }

    [Fact]
    public void Customer_document_is_validated_and_normalized()
    {
        var sale = new Sale(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => sale.SetCustomer("123.456.789-00", null));

        sale.SetCustomer("529.982.247-25", "Ana");
        Assert.Equal("52998224725", sale.CustomerDocument);

        sale.SetCustomer(" ", null);
        Assert.Null(sale.CustomerDocument);
    }
}
