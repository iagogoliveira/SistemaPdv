using Pdv.Application.Fiscal;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;

namespace Pdv.Fiscal.Tests;

public class AccessKeyTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Key_layout_and_check_digit()
    {
        var key = AccessKey.Build("SP", IssuedAt, "11.222.333/0001-81", 65, 1, 123, FiscalEmissionType.Normal, 12345678);

        Assert.Equal(44, key.Length);
        Assert.StartsWith("35" + "2610" + "11222333000181" + "65" + "001" + "000000123" + "1" + "12345678", key, StringComparison.Ordinal);
        Assert.True(AccessKey.IsValid(key));
        Assert.False(AccessKey.IsValid(key[..43] + ((key[43] - '0' + 1) % 10)));
    }

    [Fact]
    public void Key_accepts_alphanumeric_cnpj()
    {
        var key = AccessKey.Build("MG", IssuedAt, "12.ABC.345/01DE-35", 65, 2, 7, FiscalEmissionType.OfflineContingency, 87654321);
        Assert.Equal("12ABC34501DE35", key[6..20]);
        Assert.Equal('9', key[34]);
        Assert.True(AccessKey.IsValid(key));
    }

    [Fact]
    public void Random_code_differs_from_number()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = AccessKey.NewRandomCode(12345678);
            Assert.NotEqual(12345678, code);
            Assert.InRange(code, 10_000_000, 99_999_999);
        }
    }

    [Fact]
    public void Invalid_uf_is_rejected() =>
        Assert.Throws<DomainException>(() => AccessKey.Build("XX", IssuedAt, "11222333000181", 65, 1, 1, FiscalEmissionType.Normal, 1));

    [Theory]
    [InlineData("7891000315507", true)]
    [InlineData("7891000315508", false)]
    [InlineData("96385074", true)]
    [InlineData("INT-1", false)]
    [InlineData(null, false)]
    public void Gtin_check_digit(string? code, bool valid) => Assert.Equal(valid, Gtin.IsValid(code));

    [Fact]
    public void Justification_length_rules()
    {
        Assert.Throws<DomainException>(() => FiscalDocument.ValidateJustification("curta"));
        Assert.Equal("Cliente desistiu da compra", FiscalDocument.ValidateJustification("  Cliente desistiu da compra "));
    }
}
