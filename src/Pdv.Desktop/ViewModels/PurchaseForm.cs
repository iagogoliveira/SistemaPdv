using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application.Purchases;
using Pdv.Domain.Common;

namespace Pdv.Desktop.ViewModels;

/// <summary>Conferência de uma entrada de mercadorias (XML importado ou digitada).</summary>
public partial class PurchaseForm : ObservableObject
{
    private readonly Func<PurchaseLineForm, Task> _link;
    private readonly Func<PurchaseLineForm, Task> _create;
    private readonly Func<Task<PurchaseLineForm?>> _addManual;

    public PurchaseForm(PurchaseDraft draft, bool isManual, IReadOnlyList<SupplierModel> suppliers,
        Func<PurchaseLineForm, Task> link, Func<PurchaseLineForm, Task> create, Func<Task<PurchaseLineForm?>> addManual)
    {
        Draft = draft;
        IsManual = isManual;
        Suppliers = suppliers;
        _link = link;
        _create = create;
        _addManual = addManual;
        InvoiceNumber = draft.InvoiceNumber ?? "";
        Supplier = suppliers.FirstOrDefault(s => s.Id == draft.SupplierId);
        foreach (var l in draft.Lines) Add(new PurchaseLineForm(l, isManual));
    }

    public PurchaseDraft Draft { get; }
    public bool IsManual { get; }
    public bool IsFromXml => !IsManual;
    public IReadOnlyList<SupplierModel> Suppliers { get; }
    public string Title => IsManual ? "Entrada manual de mercadorias" : $"Entrada da NF-e nº {Draft.InvoiceNumber} — {Draft.SupplierName}";
    public string Header => IsManual ? "Escolha o fornecedor e adicione os itens recebidos."
        : $"Emitida em {Draft.IssuedAt?.ToLocalTime():dd/MM/yyyy} · chave {Draft.AccessKey}";
    public IReadOnlyList<string> Warnings => Draft.Warnings;
    public bool HasWarnings => Draft.Warnings.Count > 0;
    public ObservableCollection<PurchaseLineForm> Lines { get; } = [];

    [ObservableProperty] private PurchaseLineForm? _selectedLine;
    [ObservableProperty] private SupplierModel? _supplier;
    [ObservableProperty] private string _invoiceNumber;

    public decimal Total => Lines.Sum(l => l.LineTotal);
    public int Unlinked => Lines.Count(l => !l.IsLinked);
    public string Status => Unlinked == 0 ? $"{Lines.Count} item(ns) prontos para entrada." : $"{Unlinked} item(ns) sem produto vinculado.";

    [RelayCommand]
    private Task LinkSelected() => SelectedLine is null ? Task.CompletedTask : _link(SelectedLine);

    [RelayCommand]
    private Task CreateProductForSelected() => SelectedLine is null ? Task.CompletedTask : _create(SelectedLine);

    [RelayCommand]
    private async Task AddItem()
    {
        if (await _addManual() is { } line) Add(line);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedLine is not null && IsManual) Lines.Remove(SelectedLine);
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Unlinked));
        OnPropertyChanged(nameof(Status));
    }

    public PurchaseDraft ToDraft() => Draft with
    {
        SupplierId = IsManual ? Supplier?.Id : Draft.SupplierId,
        SupplierName = IsManual ? Supplier?.Name ?? "" : Draft.SupplierName,
        SupplierDocument = IsManual ? Supplier?.Document : Draft.SupplierDocument,
        InvoiceNumber = IsManual ? (string.IsNullOrWhiteSpace(InvoiceNumber) ? null : InvoiceNumber.Trim()) : Draft.InvoiceNumber,
        Lines = Lines.Select((l, i) => l.ToLine(i + 1)).ToList(),
    };

    private void Add(PurchaseLineForm line)
    {
        line.PropertyChanged += (_, _) => Refresh();
        Lines.Add(line);
        Refresh();
    }
}

public partial class PurchaseLineForm : ObservableObject
{
    public PurchaseLineForm(PurchaseLine line, bool editable)
    {
        Source = line;
        IsEditable = editable;
        _productId = line.ProductId;
        _productName = line.ProductName;
        _factorText = line.ConversionFactor.ToString("0.###", CultureInfo.CurrentCulture);
        _quantityText = line.SupplierQuantity.ToString("0.###", CultureInfo.CurrentCulture);
        _totalText = line.LineTotal.ToString("N2", CultureInfo.CurrentCulture);
    }

    public PurchaseLine Source { get; }
    public bool IsEditable { get; }
    public string Description => Source.Description;
    public string? SupplierCode => Source.SupplierCode;
    public string Unit => Source.Unit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked), nameof(LinkText))]
    private Guid? _productId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkText))]
    private string? _productName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StockQuantity), nameof(UnitCost))]
    private string _factorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StockQuantity), nameof(UnitCost), nameof(LineTotal))]
    private string _quantityText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitCost), nameof(LineTotal))]
    private string _totalText;

    public bool IsLinked => ProductId is not null;
    public string LinkText => ProductName ?? "— vincular —";
    public decimal Factor => CheckoutViewModel.TryParseDecimal(FactorText, out var f) && f > 0 ? f : 1;
    public decimal SupplierQuantity => CheckoutViewModel.TryParseDecimal(QuantityText, out var q) && q > 0 ? q : 0;
    public decimal LineTotal => CheckoutViewModel.TryParseDecimal(TotalText, out var t) && t >= 0 ? t : 0;
    public decimal StockQuantity => Money.RoundQuantity(SupplierQuantity * Factor);
    public decimal UnitCost => StockQuantity == 0 ? 0 : Math.Round(LineTotal / StockQuantity, 4, MidpointRounding.AwayFromZero);

    public PurchaseLine ToLine(int lineNumber) => Source with
    {
        LineNumber = lineNumber,
        ProductId = ProductId,
        ProductName = ProductName,
        ConversionFactor = Factor,
        SupplierQuantity = SupplierQuantity,
        LineTotal = LineTotal,
    };
}
