using System.Diagnostics.CodeAnalysis;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;

namespace Pdv.Domain.Catalog;

public class Category : Entity
{
    private Category() { Name = null!; }

    public Category(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Nome da categoria é obrigatório.");
        Name = name.Trim();
        IsActive = true;
    }

    public string Name { get; private set; }
    public bool IsActive { get; private set; }

    public void Deactivate() => IsActive = false;
}

public class Supplier : Entity
{
    private Supplier() { Name = null!; }

    public Supplier(string name, string? document, string? phone = null, string? email = null)
    {
        Update(name, document, phone, email);
        IsActive = true;
    }

    public string Name { get; private set; }

    /// <summary>CNPJ ou CPF sem pontuação (opcional, mas validado quando informado).</summary>
    public string? Document { get; private set; }

    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public bool IsActive { get; private set; }

    [MemberNotNull(nameof(Name))]
    public void Update(string name, string? document, string? phone, string? email)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Nome do fornecedor é obrigatório.");
        if (!string.IsNullOrWhiteSpace(document) && !TaxDocuments.IsValidCpfOrCnpj(document))
            throw new DomainException("CNPJ/CPF do fornecedor inválido.");
        Name = name.Trim();
        Document = string.IsNullOrWhiteSpace(document) ? null : TaxDocuments.Normalize(document);
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}

public class Product : Entity
{
    private readonly List<ProductBarcode> _barcodes = [];

    private Product() { Name = Unit = null!; }

    public Product(string name, decimal salePrice, decimal costPrice, string unit = "UN", Guid? categoryId = null)
    {
        Rename(name);
        ChangePrices(salePrice, costPrice);
        Unit = string.IsNullOrWhiteSpace(unit) ? "UN" : unit.Trim().ToUpperInvariant();
        CategoryId = categoryId;
        IsActive = true;
    }

    public string Name { get; private set; }
    public string Unit { get; private set; }
    public Guid? CategoryId { get; private set; }
    public decimal SalePrice { get; private set; }
    public decimal CostPrice { get; private set; }
    public bool IsActive { get; private set; }
    public IReadOnlyCollection<ProductBarcode> Barcodes => _barcodes;

    // ---- Dados fiscais ----
    public string? Ncm { get; private set; }
    public string? Cest { get; private set; }
    public ProductOrigin Origin { get; private set; }

    /// <summary>Vazio = usa o perfil tributário padrão da empresa.</summary>
    public Guid? TaxProfileId { get; private set; }

    public bool HasFiscalData => Ncm is not null;

    /// <summary>Estoque mínimo para alerta de reposição (vazio = sem alerta).</summary>
    public decimal? MinimumStock { get; private set; }

    public void SetMinimumStock(decimal? minimum)
    {
        if (minimum < 0) throw new DomainException("Estoque mínimo não pode ser negativo.");
        MinimumStock = minimum is null ? null : Money.RoundQuantity(minimum.Value);
    }

    public void SetFiscalData(string? ncm, string? cest, ProductOrigin origin, Guid? taxProfileId)
    {
        if (!Enum.IsDefined(origin)) throw new DomainException("Origem da mercadoria inválida.");
        Ncm = string.IsNullOrWhiteSpace(ncm) ? null : FiscalCodes.RequireNcm(ncm);
        Cest = FiscalCodes.OptionalCest(cest);
        Origin = origin;
        TaxProfileId = taxProfileId;
    }

    [MemberNotNull(nameof(Name))]
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Descrição do produto é obrigatória.");
        Name = name.Trim();
    }

    public void Update(string name, string unit, Guid? categoryId)
    {
        Rename(name);
        Unit = string.IsNullOrWhiteSpace(unit) ? "UN" : unit.Trim().ToUpperInvariant();
        CategoryId = categoryId;
    }

    public void ChangePrices(decimal salePrice, decimal costPrice)
    {
        if (salePrice < 0 || costPrice < 0) throw new DomainException("Preços não podem ser negativos.");
        SalePrice = Money.Round(salePrice);
        CostPrice = Money.Round(costPrice);
    }

    public ProductBarcode AddBarcode(string code)
    {
        var normalized = code?.Trim() ?? "";
        if (normalized.Length == 0) throw new DomainException("Código de barras inválido.");
        if (_barcodes.Any(b => b.Code == normalized)) throw new DomainException("Código de barras já vinculado ao produto.");
        var barcode = new ProductBarcode(Id, normalized);
        _barcodes.Add(barcode);
        return barcode;
    }

    public void RemoveBarcode(string code)
    {
        var barcode = _barcodes.SingleOrDefault(b => b.Code == code) ?? throw new DomainException("Código não vinculado ao produto.");
        _barcodes.Remove(barcode);
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}

public class ProductBarcode : Entity
{
    private ProductBarcode() { Code = null!; }

    internal ProductBarcode(Guid productId, string code)
    {
        ProductId = productId;
        Code = code;
    }

    public Guid ProductId { get; private set; }
    public string Code { get; private set; }
}
