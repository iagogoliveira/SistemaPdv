using Pdv.Domain.Common;

namespace Pdv.Domain.Fiscal;

/// <summary>Código de Regime Tributário (CRT) do emitente, como informado na NF-e/NFC-e.</summary>
public enum TaxRegime
{
    SimplesNacional = 1,
    SimplesNacionalExcessoSublimite = 2,
    RegimeNormal = 3,
    SimplesNacionalMei = 4,
}

/// <summary>Origem da mercadoria (tabela A do CST/CSOSN).</summary>
public enum ProductOrigin
{
    Nacional = 0,
    EstrangeiraImportacaoDireta = 1,
    EstrangeiraMercadoInterno = 2,
    NacionalConteudoImportacaoSuperior40 = 3,
    NacionalProcessosBasicos = 4,
    NacionalConteudoImportacaoAte40 = 5,
    EstrangeiraImportacaoDiretaSemSimilar = 6,
    EstrangeiraMercadoInternoSemSimilar = 7,
    NacionalConteudoImportacaoSuperior70 = 8,
}

public static class TaxRegimeExtensions
{
    /// <summary>Simples Nacional (CRT 1) e MEI (CRT 4) usam CSOSN; os demais usam CST do ICMS.</summary>
    public static bool UsesCsosn(this TaxRegime regime) => regime is TaxRegime.SimplesNacional or TaxRegime.SimplesNacionalMei;
}

public static class FiscalCodes
{
    public static readonly IReadOnlyList<string> Csosn = ["101", "102", "103", "201", "202", "203", "300", "400", "500", "900"];

    public static readonly IReadOnlyList<string> IcmsCst =
        ["00", "02", "10", "15", "20", "30", "40", "41", "50", "51", "53", "60", "61", "70", "90"];

    public static readonly IReadOnlyList<string> PisCofinsCst =
    [
        "01", "02", "03", "04", "05", "06", "07", "08", "09",
        "49", "50", "51", "52", "53", "54", "55", "56",
        "60", "61", "62", "63", "64", "65", "66", "67",
        "70", "71", "72", "73", "74", "75", "98", "99",
    ];

    public static readonly IReadOnlyList<string> Ufs =
    [
        "AC", "AL", "AM", "AP", "BA", "CE", "DF", "ES", "GO", "MA", "MG", "MS", "MT", "PA",
        "PB", "PE", "PI", "PR", "RJ", "RN", "RO", "RR", "RS", "SC", "SE", "SP", "TO",
    ];

    public static string Digits(string? value) => new((value ?? "").Where(char.IsAsciiDigit).ToArray());

    public static bool IsValidNcm(string? value) => Digits(value) is { Length: 8 } d && d == (value ?? "").Trim().Replace(".", "", StringComparison.Ordinal);

    public static bool IsValidCest(string? value) => Digits(value).Length == 7;

    /// <summary>NFC-e é sempre operação interna com consumidor final: CFOP do grupo 5.</summary>
    public static bool IsValidNfceCfop(string? value) => Digits(value) is { Length: 4 } d && d[0] == '5';

    public static string RequireNcm(string? value) =>
        IsValidNcm(value) ? Digits(value) : throw new DomainException("NCM deve ter 8 dígitos.");

    public static string? OptionalCest(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : IsValidCest(value) ? Digits(value)
        : throw new DomainException("CEST deve ter 7 dígitos.");

    public static decimal RequireRate(decimal rate, string name) =>
        rate is >= 0 and <= 100 ? Math.Round(rate, 4) : throw new DomainException($"Alíquota de {name} deve estar entre 0 e 100%.");
}
