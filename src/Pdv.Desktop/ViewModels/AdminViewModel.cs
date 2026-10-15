using Pdv.Client;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Identity;
using Pdv.Desktop.Services;
using Pdv.Desktop.Views;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Inventory;

namespace Pdv.Desktop.ViewModels;

/// <summary>Retaguarda: cadastro de produtos/estoque e de usuários. O backend revalida cada permissão.</summary>
public partial class AdminViewModel(ServerRunner runner, AppState state, IDialogService dialogs) : ObservableObject
{
    private Guid ActorId => state.User!.UserId;

    // Acesso para a outra parte da classe (AdminViewModel.Fiscal.cs).
    private ServerRunner Runner => runner;
    private IDialogService Dialogs => dialogs;

    public bool CanManageProducts => state.User!.Can(Permissions.ManageProducts);
    public bool CanAdjustStock => state.User!.Can(Permissions.AdjustStock);
    public bool CanManageUsers => state.User!.Can(Permissions.ManageUsers);
    public bool ShowProductsTab => CanManageProducts || CanAdjustStock;

    // ---------- Produtos ----------

    public ObservableCollection<ProductListItem> Products { get; } = [];

    [ObservableProperty]
    private string _productSearch = "";

    [ObservableProperty]
    private bool _includeInactive;

    [ObservableProperty]
    private ProductListItem? _selectedProduct;

    public async Task LoadAsync()
    {
        if (ShowProductsTab) await SearchProductsAsync();
        if (CanManageUsers) await LoadUsersAsync();
        await LoadFiscalAsync();
        await LoadPurchasesAsync();
        LoadReports();
        await LoadPaymentsAsync();
        await LoadSystemAsync();
    }

    partial void OnIncludeInactiveChanged(bool value) => SearchProductsCommand.Execute(null);

    [RelayCommand]
    private async Task SearchProductsAsync()
    {
        var list = await runner.RunAsync<ProductAdminService, IReadOnlyList<ProductListItem>>(s => s.ListAsync(ProductSearch, IncludeInactive));
        var selectedId = SelectedProduct?.Id;
        Products.Clear();
        foreach (var p in list) Products.Add(p);
        SelectedProduct = Products.FirstOrDefault(p => p.Id == selectedId);
    }

    [RelayCommand]
    private Task NewProductAsync() => EditProductAsync(null);

    [RelayCommand]
    private Task EditSelectedProductAsync() => SelectedProduct is null ? Task.CompletedTask : EditProductAsync(SelectedProduct.Id);

    private async Task EditProductAsync(Guid? id)
    {
        if (!Require(CanManageProducts)) return;

        var categories = await runner.RunAsync<ProductAdminService, IReadOnlyList<string>>(s => s.ListCategoriesAsync());
        var profiles = await runner.RunAsync<TaxProfileService, IReadOnlyList<TaxProfileModel>>(s => s.ListAsync());
        var form = new ProductForm(categories, profiles);
        if (id is { } productId)
        {
            var model = await runner.RunAsync<ProductAdminService, ProductEditModel?>(s => s.GetAsync(productId));
            if (model is null) return;
            form.Load(model);
        }

        while (true)
        {
            var dialog = new ProductEditDialog(form) { Owner = System.Windows.Application.Current.Windows.OfType<AdminWindow>().FirstOrDefault() };
            if (dialog.ShowDialog() != true) return;
            if (!form.TryBuild(id, out var model, out var error)) { dialogs.ShowError(error!); continue; }

            var result = await runner.RunAsync<ProductAdminService, Result<Guid>>(s => s.SaveAsync(ActorId, model!));
            if (!result.Succeeded) { dialogs.ShowError(result.Error!); continue; }
            break;
        }
        await SearchProductsAsync();
    }

    [RelayCommand]
    private async Task ToggleProductActiveAsync()
    {
        if (SelectedProduct is not { } p || !Require(CanManageProducts)) return;
        var activate = !p.IsActive;
        if (!activate && !dialogs.Confirm($"Desativar \"{p.Name}\"? Ele deixa de aparecer no caixa, mas o histórico é mantido.")) return;
        var result = await runner.RunAsync<ProductAdminService, Result<Guid>>(s => s.SetActiveAsync(ActorId, p.Id, activate));
        await ReportAsync(result, SearchProductsAsync);
    }

    [RelayCommand]
    private async Task StockEntryAsync()
    {
        if (SelectedProduct is not { } p || !Require(CanAdjustStock)) return;
        var qtyText = dialogs.Prompt("Entrada de mercadoria", $"{p.Name}\nQuantidade recebida:");
        if (qtyText is null) return;
        if (!CheckoutViewModel.TryParseDecimal(qtyText, out var qty) || qty <= 0) { dialogs.ShowError("Quantidade inválida."); return; }

        var costText = dialogs.Prompt("Entrada de mercadoria", "Custo unitário (vazio = manter atual):", p.CostPrice.ToString("N2", CultureInfo.CurrentCulture));
        if (costText is null) return;
        decimal? cost = null;
        if (!string.IsNullOrWhiteSpace(costText))
        {
            if (!CheckoutViewModel.TryParseDecimal(costText, out var c) || c < 0) { dialogs.ShowError("Custo inválido."); return; }
            cost = c;
        }
        var note = dialogs.Prompt("Entrada de mercadoria", "Documento/observação (ex.: NF 1234):") ?? "";

        var result = await runner.RunAsync<ProductAdminService, Result<Guid>>(s => s.RegisterEntryAsync(ActorId, p.Id, qty, cost, note));
        await ReportAsync(result, SearchProductsAsync);
    }

    [RelayCommand]
    private async Task AdjustStockAsync()
    {
        if (SelectedProduct is not { } p || !Require(CanAdjustStock)) return;
        var countText = dialogs.Prompt("Ajuste de estoque", $"{p.Name}\nEstoque atual no sistema: {p.Stock:0.###}\nQuantidade contada:");
        if (countText is null) return;
        if (!CheckoutViewModel.TryParseDecimal(countText, out var counted) || counted < 0) { dialogs.ShowError("Quantidade inválida."); return; }
        if (counted == p.Stock) { dialogs.ShowInfo("A quantidade contada é igual ao estoque atual."); return; }

        var reason = dialogs.Prompt("Ajuste de estoque", "Motivo do ajuste (obrigatório):");
        if (string.IsNullOrWhiteSpace(reason)) return;

        var result = await runner.RunAsync<ProductAdminService, Result<Guid>>(s => s.AdjustToCountAsync(ActorId, p.Id, counted, reason));
        await ReportAsync(result, SearchProductsAsync);
    }

    [RelayCommand]
    private async Task ShowStockHistoryAsync()
    {
        if (SelectedProduct is not { } p) return;
        var movements = await runner.RunAsync<ProductAdminService, IReadOnlyList<StockMovementListItem>>(s => s.ListMovementsAsync(p.Id));
        var rows = movements.Select(m => new StockRow(m)).ToList();
        dialogs.ShowGrid($"Movimentações — {p.Name}", rows,
        [
            new GridColumn("Data/hora", nameof(StockRow.When), Width: 140),
            new GridColumn("Tipo", nameof(StockRow.Kind), Width: 150),
            new GridColumn("Qtd", nameof(StockRow.Quantity), "0.###", 80),
            new GridColumn("Motivo/documento", nameof(StockRow.Reason)),
            new GridColumn("Usuário", nameof(StockRow.User), Width: 140),
        ]);
    }

    public sealed record StockRow(StockMovementListItem Movement)
    {
        public string When => Movement.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        public string Kind => Movement.Type switch
        {
            StockMovementType.PurchaseEntry => "Entrada",
            StockMovementType.Sale => "Venda",
            StockMovementType.SaleCancellation => "Cancelamento de venda",
            StockMovementType.Adjustment => "Ajuste",
            _ => Movement.Type.ToString(),
        };
        public decimal Quantity => Movement.Quantity;
        public string? Reason => Movement.Reason;
        public string User => Movement.UserName;
    }

    // ---------- Usuários ----------

    public ObservableCollection<UserListItem> Users { get; } = [];

    public ObservableCollection<RoleListItem> Roles { get; } = [];

    [ObservableProperty]
    private UserListItem? _selectedUser;

    private async Task LoadUsersAsync()
    {
        var users = await runner.RunAsync<UserAdminService, IReadOnlyList<UserListItem>>(s => s.ListAsync(default));
        var roles = await runner.RunAsync<UserAdminService, IReadOnlyList<RoleListItem>>(s => s.ListRolesAsync(default));
        var selectedId = SelectedUser?.Id;
        Users.Clear();
        foreach (var u in users) Users.Add(u);
        Roles.Clear();
        foreach (var r in roles) Roles.Add(r);
        SelectedUser = Users.FirstOrDefault(u => u.Id == selectedId);
    }

    [RelayCommand]
    private async Task NewUserAsync()
    {
        if (!Require(CanManageUsers)) return;
        var form = new UserForm(Roles);
        while (true)
        {
            var dialog = new UserEditDialog(form) { Owner = System.Windows.Application.Current.Windows.OfType<AdminWindow>().FirstOrDefault() };
            if (dialog.ShowDialog() != true) return;
            if (form.Role is null) { dialogs.ShowError("Selecione o perfil."); continue; }

            var result = await runner.RunAsync<UserAdminService, Result<Guid>>(s =>
                s.CreateAsync(ActorId, form.Username, form.DisplayName, dialog.Password, form.Role.Id));
            if (!result.Succeeded) { dialogs.ShowError(result.Error!); continue; }
            break;
        }
        await LoadUsersAsync();
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (SelectedUser is not { } u || !Require(CanManageUsers)) return;
        var password = dialogs.PromptPassword("Redefinir senha", $"Nova senha para {u.DisplayName} (mínimo {AuthenticationService.MinPasswordLength} caracteres):");
        if (password is null) return;
        var confirm = dialogs.PromptPassword("Redefinir senha", "Repita a nova senha:");
        if (confirm != password) { dialogs.ShowError("As senhas não conferem."); return; }

        var result = await runner.RunAsync<UserAdminService, Result<Guid>>(s => s.ResetPasswordAsync(ActorId, u.Id, password));
        if (result.Succeeded) dialogs.ShowInfo("Senha redefinida.");
        else dialogs.ShowError(result.Error!);
    }

    [RelayCommand]
    private async Task ChangeRoleAsync()
    {
        if (SelectedUser is not { } u || !Require(CanManageUsers)) return;
        var role = dialogs.Pick($"Perfil de {u.DisplayName}", Roles, [new GridColumn("Perfil", nameof(RoleListItem.Name))], r => r.Id != u.RoleId);
        if (role is null) return;
        var result = await runner.RunAsync<UserAdminService, Result<Guid>>(s => s.ChangeRoleAsync(ActorId, u.Id, role.Id));
        await ReportAsync(result, LoadUsersAsync);
    }

    [RelayCommand]
    private async Task ToggleUserActiveAsync()
    {
        if (SelectedUser is not { } u || !Require(CanManageUsers)) return;
        var activate = !u.IsActive;
        if (!activate && !dialogs.Confirm($"Desativar o usuário {u.Username}? Ele não poderá mais entrar no sistema.")) return;
        var result = await runner.RunAsync<UserAdminService, Result<Guid>>(s => s.SetActiveAsync(ActorId, u.Id, activate));
        await ReportAsync(result, LoadUsersAsync);
    }

    private bool Require(bool allowed)
    {
        if (!allowed) dialogs.ShowError("Seu usuário não tem permissão para esta operação.");
        return allowed;
    }

    private async Task ReportAsync(Result<Guid> result, Func<Task> refresh)
    {
        if (!result.Succeeded) dialogs.ShowError(result.Error!);
        await refresh();
    }
}

public partial class ProductForm(IReadOnlyList<string> categories, IReadOnlyList<TaxProfileModel> profiles) : ObservableObject
{
    public IReadOnlyList<string> Categories { get; } = categories;

    /// <summary>Primeira opção (sem Id) = usar o perfil padrão da empresa.</summary>
    public IReadOnlyList<Choice<Guid?>> TaxProfiles { get; } =
        [new(null, "Padrão da empresa"), .. profiles.Select(p => new Choice<Guid?>(p.Id, $"{p.Name}  ·  CFOP {p.Cfop}  ·  {p.IcmsCode}"))];

    public IReadOnlyList<Choice<ProductOrigin>> Origins { get; } = FiscalLabels.Origins;

    [ObservableProperty] private string _minimumStock = "";
    [ObservableProperty] private string _ncm = "";
    [ObservableProperty] private string _cest = "";
    [ObservableProperty] private Choice<ProductOrigin>? _origin = FiscalLabels.Origins[0];
    [ObservableProperty] private Choice<Guid?>? _taxProfile;

    [ObservableProperty] private string _title = "Novo produto";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _unit = "UN";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _salePrice = "";
    [ObservableProperty] private string _costPrice = "";
    [ObservableProperty] private string _barcodes = "";

    public void Load(ProductEditModel m)
    {
        Title = "Editar produto";
        MinimumStock = m.MinimumStock?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
        Ncm = m.Ncm is { Length: 8 } n ? $"{n[..4]}.{n[4..6]}.{n[6..]}" : m.Ncm ?? "";
        Cest = m.Cest ?? "";
        Origin = FiscalLabels.Origins.First(o => o.Value == m.Origin);
        TaxProfile = TaxProfiles.FirstOrDefault(t => t.Value == m.TaxProfileId) ?? TaxProfiles[0];
        Name = m.Name;
        Unit = m.Unit;
        Category = m.CategoryName ?? "";
        SalePrice = m.SalePrice.ToString("N2", CultureInfo.CurrentCulture);
        CostPrice = m.CostPrice.ToString("N2", CultureInfo.CurrentCulture);
        Barcodes = string.Join(Environment.NewLine, m.Barcodes);
    }

    public bool TryBuild(Guid? id, out ProductEditModel? model, out string? error)
    {
        model = null;
        error = null;
        if (string.IsNullOrWhiteSpace(Name)) { error = "Informe a descrição."; return false; }
        if (!CheckoutViewModel.TryParseDecimal(SalePrice, out var sale) || sale < 0) { error = "Preço de venda inválido."; return false; }
        var cost = 0m;
        if (!string.IsNullOrWhiteSpace(CostPrice) && (!CheckoutViewModel.TryParseDecimal(CostPrice, out cost) || cost < 0))
        {
            error = "Custo inválido.";
            return false;
        }
        decimal? minimum = null;
        if (!string.IsNullOrWhiteSpace(MinimumStock))
        {
            if (!CheckoutViewModel.TryParseDecimal(MinimumStock, out var min) || min < 0) { error = "Estoque mínimo inválido."; return false; }
            minimum = min;
        }
        var codes = Barcodes.Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        model = new ProductEditModel(id, Name, Unit, string.IsNullOrWhiteSpace(Category) ? null : Category, sale, cost, codes,
            string.IsNullOrWhiteSpace(Ncm) ? null : Ncm, string.IsNullOrWhiteSpace(Cest) ? null : Cest,
            Origin?.Value ?? ProductOrigin.Nacional, TaxProfile?.Value, minimum);
        return true;
    }
}

public partial class UserForm(IEnumerable<RoleListItem> roles) : ObservableObject
{
    public IReadOnlyList<RoleListItem> Roles { get; } = roles.ToList();

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _displayName = "";
    // Novo usuário começa como Operador (menor permissão).
    [ObservableProperty] private RoleListItem? _role = roles.FirstOrDefault(r => r.Name == "Operador");
}
