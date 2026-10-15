using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Pdv.Application.Sales;
using Pdv.Domain.Common;
using Pdv.Domain.Sales;

namespace Pdv.Desktop.ViewModels;

/// <summary>Tela de devolução/troca: quanto devolver de cada item, como devolver o valor e o motivo.</summary>
public partial class ReturnForm : ObservableObject
{
    public ReturnForm(ReturnableSale sale)
    {
        Sale = sale;
        foreach (var line in sale.Lines.Where(l => l.Available > 0))
        {
            var row = new ReturnLineForm(line);
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Total));
            Lines.Add(row);
        }
    }

    public ReturnableSale Sale { get; }
    public string Title => $"Devolução / troca — venda nº {Sale.Number}";
    public string SaleInfo => $"Venda de {Sale.CompletedAt?.ToLocalTime():dd/MM/yyyy HH:mm} · total {Sale.Total:C}";
    public ObservableCollection<ReturnLineForm> Lines { get; } = [];

    [ObservableProperty] private bool _refundInCash;
    [ObservableProperty] private bool _refundInVoucher = true;
    [ObservableProperty] private string _reason = "";

    /// <summary>Estimativa (o valor final é calculado pelo sistema, sem sobra de centavos).</summary>
    public decimal Total => Lines.Sum(l => l.Estimated);

    public RefundMethod Method => RefundInCash ? RefundMethod.Cash : RefundMethod.ExchangeVoucher;

    public bool TryBuild(out Dictionary<int, decimal> quantities, out string? error)
    {
        quantities = [];
        error = null;
        foreach (var l in Lines)
        {
            if (string.IsNullOrWhiteSpace(l.QuantityText)) continue;
            if (!CheckoutViewModel.TryParseDecimal(l.QuantityText, out var q) || q < 0 || q > l.Line.Available)
            {
                error = $"Quantidade inválida para {l.Line.Description} (disponível: {l.Line.Available:0.###}).";
                return false;
            }
            if (q > 0) quantities[l.Line.LineNumber] = q;
        }
        if (quantities.Count == 0) error = "Informe a quantidade a devolver em ao menos um item.";
        else if (string.IsNullOrWhiteSpace(Reason)) error = "Informe o motivo da devolução.";
        return error is null;
    }
}

public partial class ReturnLineForm(ReturnableLine line) : ObservableObject
{
    public ReturnableLine Line { get; } = line;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Estimated))]
    private string _quantityText = "";

    public decimal Estimated =>
        CheckoutViewModel.TryParseDecimal(QuantityText, out var q) && q > 0 && q <= Line.Available ? Money.Round(q * Line.UnitPaid) : 0;

    public string Sold => Line.SoldQuantity.ToString("0.###", CultureInfo.CurrentCulture);
    public string Available => Line.Available.ToString("0.###", CultureInfo.CurrentCulture);
}
