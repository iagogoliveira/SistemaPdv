using Pdv.Domain.Common;

namespace Pdv.Domain.Fiscal;

/// <summary>
/// Linha da tabela IBPT ("De olho no imposto"): carga tributária aproximada por NCM, usada para informar ao consumidor
/// o "Valor aproximado dos tributos" exigido pela Lei 12.741/2012. Percentuais sobre o valor do item.
/// </summary>
public class TaxApproximation : Entity
{
    private TaxApproximation() { Ncm = Version = Source = null!; }

    public TaxApproximation(string ncm, string? ex, string? description, decimal federalNational, decimal federalImported, decimal state,
        decimal municipal, DateOnly validFrom, DateOnly validTo, string version, string source)
    {
        if (ncm.Length != 8 || !ncm.All(char.IsAsciiDigit)) throw new DomainException($"NCM inválido na tabela IBPT: {ncm}.");
        if (new[] { federalNational, federalImported, state, municipal }.Any(r => r is < 0 or > 100))
            throw new DomainException($"Alíquota fora do intervalo na tabela IBPT (NCM {ncm}).");
        Ncm = ncm;
        Exception = string.IsNullOrWhiteSpace(ex) ? null : ex.Trim();
        Description = description?.Trim() is { Length: > 0 } d ? (d.Length > 200 ? d[..200] : d) : null;
        FederalNational = federalNational;
        FederalImported = federalImported;
        State = state;
        Municipal = municipal;
        ValidFrom = validFrom;
        ValidTo = validTo;
        Version = version;
        Source = source;
    }

    public string Ncm { get; private set; }
    /// <summary>Exceção ("ex") da TIPI para o mesmo NCM.</summary>
    public string? Exception { get; private set; }
    public string? Description { get; private set; }
    public decimal FederalNational { get; private set; }
    public decimal FederalImported { get; private set; }
    public decimal State { get; private set; }
    public decimal Municipal { get; private set; }
    public DateOnly ValidFrom { get; private set; }
    public DateOnly ValidTo { get; private set; }
    public string Version { get; private set; }
    public string Source { get; private set; }

    /// <summary>Origens 1, 2, 6 e 7 são estrangeiras (alíquota federal de importados); as demais, nacionais.</summary>
    public static bool IsImported(ProductOrigin origin) => origin is ProductOrigin.EstrangeiraImportacaoDireta
        or ProductOrigin.EstrangeiraMercadoInterno or ProductOrigin.EstrangeiraImportacaoDiretaSemSimilar
        or ProductOrigin.EstrangeiraMercadoInternoSemSimilar;

    public decimal FederalFor(ProductOrigin origin) => IsImported(origin) ? FederalImported : FederalNational;
}

/// <summary>Valor aproximado dos tributos (Lei 12.741/2012) de um item ou de uma venda.</summary>
public sealed record ApproximateTaxes(decimal Federal, decimal State, decimal Municipal)
{
    public static ApproximateTaxes Zero { get; } = new(0, 0, 0);

    public decimal Total => Federal + State + Municipal;

    public ApproximateTaxes Add(ApproximateTaxes other) => new(Federal + other.Federal, State + other.State, Municipal + other.Municipal);

    public static ApproximateTaxes Compute(TaxApproximation? rates, ProductOrigin origin, decimal amount) => rates is null
        ? Zero
        : new(Money.Round(amount * rates.FederalFor(origin) / 100m), Money.Round(amount * rates.State / 100m),
            Money.Round(amount * rates.Municipal / 100m));

    /// <summary>Texto exigido no documento ao consumidor.</summary>
    public string ToText(string source, System.Globalization.CultureInfo culture) => string.Format(culture,
        "Trib aprox R$ {0:N2} Federal, R$ {1:N2} Estadual e R$ {2:N2} Municipal. Fonte: {3}", Federal, State, Municipal, source);
}
