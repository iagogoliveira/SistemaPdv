using Pdv.Domain.Fiscal;

namespace Pdv.Desktop.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Textos em português para os códigos fiscais exibidos nas telas.</summary>
public static class FiscalLabels
{
    public static IReadOnlyList<Choice<TaxRegime>> Regimes { get; } =
    [
        new(TaxRegime.SimplesNacional, "1 – Simples Nacional"),
        new(TaxRegime.SimplesNacionalMei, "4 – Simples Nacional (MEI)"),
        new(TaxRegime.SimplesNacionalExcessoSublimite, "2 – Simples Nacional, excesso de sublimite"),
        new(TaxRegime.RegimeNormal, "3 – Regime normal (Lucro Presumido/Real)"),
    ];

    public static IReadOnlyList<Choice<ProductOrigin>> Origins { get; } =
    [
        new(ProductOrigin.Nacional, "0 – Nacional"),
        new(ProductOrigin.EstrangeiraImportacaoDireta, "1 – Estrangeira, importação direta"),
        new(ProductOrigin.EstrangeiraMercadoInterno, "2 – Estrangeira, adquirida no mercado interno"),
        new(ProductOrigin.NacionalConteudoImportacaoSuperior40, "3 – Nacional, importação > 40% e ≤ 70%"),
        new(ProductOrigin.NacionalProcessosBasicos, "4 – Nacional, processos produtivos básicos"),
        new(ProductOrigin.NacionalConteudoImportacaoAte40, "5 – Nacional, importação ≤ 40%"),
        new(ProductOrigin.EstrangeiraImportacaoDiretaSemSimilar, "6 – Estrangeira, importação direta, sem similar"),
        new(ProductOrigin.EstrangeiraMercadoInternoSemSimilar, "7 – Estrangeira, mercado interno, sem similar"),
        new(ProductOrigin.NacionalConteudoImportacaoSuperior70, "8 – Nacional, importação > 70%"),
    ];

    private static readonly Dictionary<string, string> CsosnText = new()
    {
        ["101"] = "Tributada com permissão de crédito",
        ["102"] = "Tributada sem permissão de crédito",
        ["103"] = "Isenção para faixa de receita bruta",
        ["201"] = "Com crédito e cobrança de ICMS-ST",
        ["202"] = "Sem crédito e com cobrança de ICMS-ST",
        ["203"] = "Isenção para faixa de receita, com ICMS-ST",
        ["300"] = "Imune",
        ["400"] = "Não tributada",
        ["500"] = "ICMS cobrado anteriormente por ST ou antecipação",
        ["900"] = "Outros",
    };

    private static readonly Dictionary<string, string> IcmsCstText = new()
    {
        ["00"] = "Tributada integralmente",
        ["02"] = "Monofásico próprio (combustíveis)",
        ["10"] = "Tributada com cobrança de ICMS-ST",
        ["15"] = "Monofásico próprio e com retenção (combustíveis)",
        ["20"] = "Com redução de base de cálculo",
        ["30"] = "Isenta/não tributada com ICMS-ST",
        ["40"] = "Isenta",
        ["41"] = "Não tributada",
        ["50"] = "Suspensão",
        ["51"] = "Diferimento",
        ["53"] = "Monofásico com diferimento (combustíveis)",
        ["60"] = "ICMS cobrado anteriormente por ST",
        ["61"] = "Monofásico cobrado anteriormente (combustíveis)",
        ["70"] = "Redução de base de cálculo com ICMS-ST",
        ["90"] = "Outras",
    };

    private static readonly Dictionary<string, string> PisCofinsText = new()
    {
        ["01"] = "Tributável, alíquota básica",
        ["02"] = "Tributável, alíquota diferenciada",
        ["04"] = "Monofásica, revenda a alíquota zero",
        ["05"] = "Substituição tributária",
        ["06"] = "Alíquota zero",
        ["07"] = "Isenta",
        ["08"] = "Sem incidência",
        ["09"] = "Suspensão",
        ["49"] = "Outras operações de saída",
        ["99"] = "Outras operações",
    };

    public static IReadOnlyList<Choice<string>> Csosn { get; } = Build(FiscalCodes.Csosn, CsosnText);
    public static IReadOnlyList<Choice<string>> IcmsCst { get; } = Build(FiscalCodes.IcmsCst, IcmsCstText);
    public static IReadOnlyList<Choice<string>> PisCofinsCst { get; } = Build(FiscalCodes.PisCofinsCst, PisCofinsText);

    public static IReadOnlyList<string> Ufs => FiscalCodes.Ufs;

    public static string RegimeLabel(TaxRegime regime) => Regimes.First(r => r.Value == regime).Label;

    private static Choice<string>[] Build(IEnumerable<string> codes, Dictionary<string, string> text) =>
        codes.Select(c => new Choice<string>(c, text.TryGetValue(c, out var t) ? $"{c} – {t}" : c)).ToArray();
}
