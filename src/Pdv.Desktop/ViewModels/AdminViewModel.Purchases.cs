using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Purchases;
using Pdv.Desktop.Views;
using Pdv.Domain.Purchases;

namespace Pdv.Desktop.ViewModels;

/// <summary>Retaguarda — aba Compras: fornecedores e entradas de mercadorias (XML da NF-e ou manual).</summary>
public partial class AdminViewModel
{
    public ObservableCollection<SupplierModel> Suppliers { get; } = [];
    public ObservableCollection<PurchaseListItem> Purchases { get; } = [];

    [ObservableProperty] private SupplierModel? _selectedSupplier;

    private async Task LoadPurchasesAsync()
    {
        if (!ShowProductsTab) return;
        var suppliers = await Runner.RunAsync<SupplierService, IReadOnlyList<SupplierModel>>(s => s.ListAsync(includeInactive: true));
        var selected = SelectedSupplier?.Id;
        Suppliers.Clear();
        foreach (var s in suppliers) Suppliers.Add(s);
        SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == selected);

        var purchases = await Runner.RunAsync<PurchaseService, IReadOnlyList<PurchaseListItem>>(s => s.ListRecentAsync());
        Purchases.Clear();
        foreach (var p in purchases) Purchases.Add(p);
    }

    [RelayCommand]
    private Task NewSupplierAsync() => EditSupplierAsync(null);

    [RelayCommand]
    private Task EditSelectedSupplierAsync() => SelectedSupplier is null ? Task.CompletedTask : EditSupplierAsync(SelectedSupplier);

    private async Task EditSupplierAsync(SupplierModel? existing)
    {
        while (true)
        {
            var dialog = new SupplierEditDialog(existing) { Owner = AdminOwner };
            if (dialog.ShowDialog() != true) return;
            var result = await Runner.RunAsync<SupplierService, Result<Guid>>(s => s.SaveAsync(ActorId, dialog.Model));
            if (result.Succeeded) break;
            Dialogs.ShowError(result.Error!);
            existing = dialog.Model;
        }
        await LoadPurchasesAsync();
    }

    [RelayCommand]
    private async Task ToggleSupplierActiveAsync()
    {
        if (SelectedSupplier is not { Id: { } id } s) return;
        var result = await Runner.RunAsync<SupplierService, Result<Guid>>(svc => svc.SetActiveAsync(ActorId, id, !s.IsActive));
        if (!result.Succeeded) Dialogs.ShowError(result.Error!);
        await LoadPurchasesAsync();
    }

    [RelayCommand]
    private async Task ImportPurchaseXmlAsync()
    {
        if (!Require(CanAdjustStock)) return;
        var file = Dialogs.PickFile("XML da NF-e do fornecedor", "NF-e (*.xml)|*.xml");
        if (file is null) return;

        var preview = await Runner.RunAsync<PurchaseService, Result<PurchaseDraft>>(s => s.PreviewXmlAsync(File.ReadAllText(file)));
        if (!preview.Succeeded) { Dialogs.ShowError(preview.Error!); return; }
        await RunPurchaseDialogAsync(preview.Value!, isManual: false);
    }

    [RelayCommand]
    private async Task ManualPurchaseAsync()
    {
        if (!Require(CanAdjustStock)) return;
        if (Suppliers.All(s => !s.IsActive)) { Dialogs.ShowError("Cadastre um fornecedor antes da entrada manual."); return; }
        var draft = new PurchaseDraft(null, "", null, null, null, null, null, [], []);
        await RunPurchaseDialogAsync(draft, isManual: true);
    }

    private async Task RunPurchaseDialogAsync(PurchaseDraft draft, bool isManual)
    {
        var form = new PurchaseForm(draft, isManual, Suppliers.Where(s => s.IsActive).ToList(), LinkLineAsync, CreateProductForLineAsync, AddManualLineAsync);
        while (true)
        {
            var dialog = new PurchaseDialog(form) { Owner = AdminOwner };
            if (dialog.ShowDialog() != true) return;
            var result = await Runner.RunAsync<PurchaseService, Result<Guid>>(s => s.ConfirmAsync(ActorId, form.ToDraft()));
            if (result.Succeeded) break;
            Dialogs.ShowError(result.Error!);
        }
        Dialogs.ShowInfo($"Entrada confirmada: {form.Lines.Count} item(ns), {form.Total:C}.\nEstoque e custos atualizados.");
        await LoadPurchasesAsync();
        await SearchProductsAsync();
    }

    private async Task<ProductListItem?> PickProductAsync(string title, string? suggestion)
    {
        var term = Dialogs.Prompt(title, "Buscar produto da loja (nome ou código de barras):", suggestion ?? "");
        if (term is null) return null;
        var found = await Runner.RunAsync<ProductAdminService, IReadOnlyList<ProductListItem>>(s => s.ListAsync(term, false, 50));
        if (found.Count == 0) { Dialogs.ShowError("Nenhum produto encontrado."); return null; }
        return Dialogs.Pick(title, found,
            [new GridColumn("Produto", nameof(ProductListItem.Name)), new GridColumn("Códigos", nameof(ProductListItem.Barcodes), Width: 200),
             new GridColumn("Un", nameof(ProductListItem.Unit), Width: 50), new GridColumn("Custo", nameof(ProductListItem.CostPrice), "C", 100)]);
    }

    private async Task LinkLineAsync(PurchaseLineForm line)
    {
        var suggestion = line.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (await PickProductAsync($"Vincular: {line.Description}", suggestion) is not { } product) return;
        line.ProductId = product.Id;
        line.ProductName = product.Name;
    }

    private async Task CreateProductForLineAsync(PurchaseLineForm line)
    {
        if (!Require(CanManageProducts)) return;
        var categories = await Runner.RunAsync<ProductAdminService, IReadOnlyList<string>>(s => s.ListCategoriesAsync());
        var profiles = await Runner.RunAsync<TaxProfileService, IReadOnlyList<TaxProfileModel>>(s => s.ListAsync());
        var form = new ProductForm(categories, profiles);
        form.Load(new ProductEditModel(null, CultureInfo.CurrentCulture.TextInfo.ToTitleCase(line.Description.ToLowerInvariant()),
            line.Unit is "CX" or "PCT" ? "UN" : line.Unit, null, 0, line.UnitCost,
            line.Source.Gtin is { } gtin ? [gtin] : [], line.Source.Ncm));
        form.Title = "Novo produto (a partir da nota)";
        form.SalePrice = "";

        while (true)
        {
            var dialog = new ProductEditDialog(form) { Owner = AdminOwner };
            if (dialog.ShowDialog() != true) return;
            if (!form.TryBuild(null, out var model, out var error)) { Dialogs.ShowError(error!); continue; }
            if (model!.SalePrice <= 0) { Dialogs.ShowError("Informe o preço de venda."); continue; }
            var result = await Runner.RunAsync<ProductAdminService, Result<Guid>>(s => s.SaveAsync(ActorId, model));
            if (!result.Succeeded) { Dialogs.ShowError(result.Error!); continue; }
            line.ProductId = result.Value;
            line.ProductName = model.Name;
            return;
        }
    }

    private async Task<PurchaseLineForm?> AddManualLineAsync()
    {
        if (await PickProductAsync("Adicionar item à entrada", null) is not { } product) return null;
        var qtyText = Dialogs.Prompt("Adicionar item", $"{product.Name}\nQuantidade recebida ({product.Unit}):");
        if (qtyText is null || !CheckoutViewModel.TryParseDecimal(qtyText, out var qty) || qty <= 0) return null;
        var costText = Dialogs.Prompt("Adicionar item", "Custo unitário (R$):", product.CostPrice.ToString("N2", CultureInfo.CurrentCulture));
        if (costText is null || !CheckoutViewModel.TryParseDecimal(costText, out var cost) || cost < 0) return null;
        var line = new PurchaseLine(0, null, null, product.Name, null, product.Unit, qty, Math.Round(qty * cost, 2), product.Id, product.Name);
        return new PurchaseLineForm(line, editable: true);
    }

    private static System.Windows.Window? AdminOwner => System.Windows.Application.Current.Windows.OfType<AdminWindow>().FirstOrDefault();

    public static string PurchaseStatusLabel(PurchaseStatus status) => status == PurchaseStatus.Confirmed ? "Confirmada" : "Rascunho";
}
