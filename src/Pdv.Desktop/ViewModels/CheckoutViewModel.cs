using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Sales;
using Pdv.Desktop.Services;
using Pdv.Desktop.Views;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Sales;
using Serilog;

namespace Pdv.Desktop.ViewModels;

public sealed record PaymentOption(PaymentMethod Method, string Label);

public sealed record PaymentEntry(PaymentMethod Method, string Label, decimal Amount);

public partial class CartLine(int number, ProductLookupItem product, decimal quantity) : ObservableObject
{
    public int Number { get; } = number;
    public ProductLookupItem Product { get; } = product;
    public decimal Quantity { get; } = quantity;

    // Mesmo arredondamento do domínio, para o total exibido bater com o gravado.
    public decimal GrossTotal => Money.Round(Quantity * Product.SalePrice);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Total))]
    private decimal _discount;

    public decimal Total => GrossTotal - Discount;

    public CartLine WithNumber(int number) => new(number, Product, Quantity) { Discount = Discount };
}

public partial class CheckoutViewModel(
    ScopedRunner runner,
    AppState state,
    IDialogService dialogs,
    SupervisorAuthorizer authorizer,
    ReceiptPrinter printer,
    Func<AdminViewModel> adminFactory) : ObservableObject
{
    // Chave de idempotência do carrinho atual. Só muda depois de uma venda concluída ou carrinho descartado,
    // então reenviar após timeout/erro de rede nunca duplica a venda.
    private Guid _clientRequestId = Guid.CreateVersion7();

    // Após falha de comunicação não sabemos se a venda foi gravada: o carrinho fica travado até reenviar.
    private bool _outcomeUnknown;

    // Quem liberou os descontos desta venda (o próprio operador, se tiver permissão).
    private Guid? _discountAuthorizerId;

    public ObservableCollection<CartLine> Items { get; } = [];

    public ObservableCollection<PaymentEntry> Payments { get; } = [];

    public IReadOnlyList<PaymentOption> PaymentOptions { get; } =
    [
        new(PaymentMethod.Cash, PaymentLabels.For(PaymentMethod.Cash)),
        new(PaymentMethod.Pix, PaymentLabels.For(PaymentMethod.Pix)),
        new(PaymentMethod.DebitCard, PaymentLabels.For(PaymentMethod.DebitCard)),
        new(PaymentMethod.CreditCard, PaymentLabels.For(PaymentMethod.CreditCard)),
    ];

    [ObservableProperty]
    private string _codeInput = "";

    [ObservableProperty]
    private CartLine? _selectedItem;

    [ObservableProperty]
    private PaymentOption? _selectedPayment;

    [ObservableProperty]
    private string _amountInput = "";

    [ObservableProperty]
    private string _statusMessage = "Caixa livre";

    /// <summary>CPF/CNPJ do consumidor para sair na nota (sem pontuação).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomerDocumentDisplay), nameof(HasCustomerDocument))]
    private string? _customerDocument;

    public string CustomerDocumentDisplay => CustomerDocument is { Length: 11 } ? $"CPF {TaxDocuments.Format(CustomerDocument)}"
        : CustomerDocument is null ? "" : $"CNPJ {TaxDocuments.Format(CustomerDocument)}";

    public bool HasCustomerDocument => CustomerDocument is not null;

    /// <summary>Último item lido, em destaque para o cliente acompanhar.</summary>
    [ObservableProperty]
    private CartLine? _lastItem;

    [ObservableProperty]
    private string _cashStatus = "";

    [ObservableProperty]
    private bool _isCashOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinalizeCommand))]
    private bool _isBusy;

    public string Header => $"Terminal {state.Terminal.Code}  ·  Operador: {state.User?.DisplayName}";

    public string TerminalLabel => $"Caixa {state.Terminal.Code}";

    public string OperatorName => state.User?.DisplayName ?? "";

    public bool CanOpenAdmin => state.User is { } u &&
        (u.Can(Permissions.ManageProducts) || u.Can(Permissions.AdjustStock) || u.Can(Permissions.ManageUsers));

    public decimal Subtotal => Items.Sum(i => i.GrossTotal);
    public decimal DiscountTotal => Items.Sum(i => i.Discount);
    public decimal Total => Items.Sum(i => i.Total);
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Remaining => Math.Max(0, Total - Paid);
    public decimal Change => Math.Max(0, Paid - Total);
    public int ItemCount => Items.Count;

    public async Task LoadAsync()
    {
        SelectedPayment = PaymentOptions[0];
        Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (CartLine line in e.NewItems) line.PropertyChanged += (_, _) => NotifyTotals();
            NotifyTotals();
        };
        Payments.CollectionChanged += (_, _) => NotifyTotals();
        await RefreshCashAsync();
        if (!IsCashOpen) await OpenCashAsync();
    }

    // ---------- Itens ----------

    /// <summary>Aceita "código", "quantidade*código" ou parte da descrição.</summary>
    [RelayCommand]
    private async Task AddItemAsync()
    {
        var input = CodeInput.Trim();
        if (input.Length == 0) return;
        if (!EnsureCashOpen() || !EnsureCartEditable()) return;

        var quantity = 1m;
        var star = input.IndexOf('*', StringComparison.Ordinal);
        if (star > 0)
        {
            if (!TryParseDecimal(input[..star], out quantity) || quantity <= 0)
            {
                StatusMessage = "Quantidade inválida.";
                return;
            }
            input = input[(star + 1)..].Trim();
        }

        var (product, matches) = await runner.RunAsync<ProductLookupService, (ProductLookupItem?, IReadOnlyList<ProductLookupItem>)>(async lookup =>
        {
            var byCode = await lookup.FindByBarcodeAsync(input);
            return byCode is not null ? (byCode, []) : (null, await lookup.SearchAsync(input, take: 30));
        });

        product ??= matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => dialogs.Pick("Selecione o produto", matches,
                [new GridColumn("Descrição", nameof(ProductLookupItem.Name)), new GridColumn("Un", nameof(ProductLookupItem.Unit), Width: 50),
                 new GridColumn("Preço", nameof(ProductLookupItem.SalePrice), "C", 110)]),
        };

        if (product is null)
        {
            StatusMessage = matches.Count == 0 ? $"Produto não encontrado: {input}" : "Nenhum produto selecionado.";
            return;
        }

        var line = new CartLine(Items.Count + 1, product, quantity);
        Items.Add(line);
        Payments.Clear();
        SelectedItem = line;
        LastItem = line;
        CodeInput = "";
        StatusMessage = $"{product.Name} — {quantity:0.###} × {product.SalePrice:C}";
    }

    [RelayCommand]
    private void RemoveSelectedItem()
    {
        if (SelectedItem is null || !EnsureCartEditable()) return;
        if (ReferenceEquals(LastItem, SelectedItem)) LastItem = null;
        Items.Remove(SelectedItem);
        Payments.Clear();
        Renumber();
        StatusMessage = "Item removido.";
    }

    /// <summary>Desconto no item selecionado: valor ("2,50") ou percentual ("10%").</summary>
    [RelayCommand]
    private async Task DiscountItemAsync()
    {
        var line = SelectedItem ?? Items.LastOrDefault();
        if (line is null || !EnsureCartEditable()) return;

        var input = dialogs.Prompt("Desconto no item",
            $"{line.Product.Name} — {line.GrossTotal:C}\nDesconto em R$ ou % (ex.: 2,50 ou 10%). Zero remove o desconto:",
            line.Discount > 0 ? line.Discount.ToString("N2", CultureInfo.CurrentCulture) : "");
        if (input is null) return;

        decimal discount;
        var text = input.Trim();
        if (text.EndsWith('%'))
        {
            if (!TryParseDecimal(text[..^1], out var percent) || percent < 0 || percent > 100) { dialogs.ShowError("Percentual inválido."); return; }
            discount = Money.Round(line.GrossTotal * percent / 100);
        }
        else if (!TryParseDecimal(text, out discount) || discount < 0 || discount > line.GrossTotal)
        {
            dialogs.ShowError("Desconto inválido para o item.");
            return;
        }

        if (discount > 0 && _discountAuthorizerId is null)
        {
            _discountAuthorizerId = await authorizer.AuthorizeAsync(Permissions.ApplyDiscount, "O desconto");
            if (_discountAuthorizerId is null) return;
        }

        line.Discount = discount;
        Payments.Clear();
        StatusMessage = discount > 0 ? $"Desconto de {discount:C} em {line.Product.Name}." : "Desconto removido.";
    }

    [RelayCommand]
    private void ClearCart()
    {
        if (Items.Count == 0 || !EnsureCartEditable() || !dialogs.Confirm("Descartar a venda em andamento?")) return;
        ResetCart("Venda descartada.");
    }

    /// <summary>"CPF na nota": informa (ou remove, se vazio) o CPF/CNPJ do consumidor da venda atual.</summary>
    [RelayCommand]
    private void SetCustomerDocument()
    {
        if (!EnsureCartEditable()) return;
        var input = dialogs.Prompt("CPF/CNPJ na nota", "CPF ou CNPJ do consumidor (deixe vazio para remover):",
            CustomerDocument is null ? "" : TaxDocuments.Format(CustomerDocument));
        if (input is null) return;
        if (string.IsNullOrWhiteSpace(input))
        {
            CustomerDocument = null;
            StatusMessage = "CPF/CNPJ removido da venda.";
            return;
        }
        if (!TaxDocuments.IsValidCpfOrCnpj(input))
        {
            dialogs.ShowError("CPF ou CNPJ inválido. Confira os números com o cliente.");
            return;
        }
        CustomerDocument = TaxDocuments.Normalize(input);
        StatusMessage = $"{CustomerDocumentDisplay} será informado na nota.";
    }

    // ---------- Pagamento ----------

    /// <summary>Adiciona um pagamento (vazio = valor restante). Se quitar a venda, finaliza.</summary>
    [RelayCommand]
    private async Task AddPaymentAsync()
    {
        if (Items.Count == 0 || SelectedPayment is null || !EnsureCartEditable()) return;
        if (!TryReadAmount(out var amount)) return;

        if (amount > 0)
        {
            if (SelectedPayment.Method != PaymentMethod.Cash && amount > Remaining)
            {
                dialogs.ShowError("Cartão e Pix não podem passar do valor restante (não há troco).");
                return;
            }
            Payments.Add(new PaymentEntry(SelectedPayment.Method, SelectedPayment.Label, amount));
        }

        AmountInput = "";
        if (Remaining == 0) await FinalizeAsync();
        else StatusMessage = $"Restam {Remaining:C}.";
    }

    [RelayCommand]
    private void RemovePayment(PaymentEntry? payment)
    {
        if (payment is not null && EnsureCartEditable()) Payments.Remove(payment);
    }

    [RelayCommand(CanExecute = nameof(CanFinalize))]
    private async Task FinalizeAsync()
    {
        if (!EnsureCashOpen()) return;

        // Sem pagamentos lançados: usa a forma selecionada com o valor digitado (ou o total).
        if (Payments.Count == 0 && SelectedPayment is not null)
        {
            if (!TryReadAmount(out var amount)) return;
            if (amount > 0) Payments.Add(new PaymentEntry(SelectedPayment.Method, SelectedPayment.Label, amount));
            AmountInput = "";
        }
        if (Remaining > 0)
        {
            dialogs.ShowError($"Faltam {Remaining:C} para concluir a venda.");
            return;
        }

        var command = new FinalizeSaleCommand(
            _clientRequestId,
            state.Terminal.Id,
            state.User!.UserId,
            Items.Select(i => new CheckoutItem(i.Product.ProductId, i.Quantity, i.Discount)).ToList(),
            Payments.Select(p => new CheckoutPayment(p.Method, p.Amount)).ToList(),
            DiscountTotal > 0 ? _discountAuthorizerId : null,
            CustomerDocument);

        IsBusy = true;
        SaleReceipt receipt;
        try
        {
            var result = await runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(command));
            if (!result.Succeeded)
            {
                dialogs.ShowError(result.Error!);
                await RefreshCashAsync();
                return;
            }
            _outcomeUnknown = false;
            receipt = result.Value!;
        }
        catch (Exception ex)
        {
            // Mantém o carrinho e a mesma chave: o operador pode tentar de novo sem risco de duplicar.
            _outcomeUnknown = true;
            Log.Error(ex, "Falha ao finalizar venda {RequestId}", _clientRequestId);
            dialogs.ShowError("Não foi possível concluir a venda (falha de comunicação com o banco).\nTente novamente com F10: a venda não será duplicada.");
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Log.Information("Venda {Number} ({SaleId}) concluída: {Total} (repetida: {Repeated})",
            receipt.Number, receipt.SaleId, receipt.Total, receipt.AlreadyProcessed);
        ResetCart(receipt.Change > 0 ? $"Venda nº {receipt.Number} concluída. TROCO: {receipt.Change:C}" : $"Venda nº {receipt.Number} concluída.");
        await RefreshCashAsync();
        await PrintAfterSaleAsync(receipt);
    }

    private bool CanFinalize() => !IsBusy && Items.Count > 0;

    // ---------- Comprovante e vendas gravadas ----------

    private async Task PrintAfterSaleAsync(SaleReceipt sale)
    {
        var mode = printer.Options.Mode;
        if (mode == ReceiptPrintMode.Never) return;
        var change = sale.Change > 0 ? $"TROCO: {sale.Change:C}\n\n" : "";
        if (mode == ReceiptPrintMode.Ask && !dialogs.Confirm($"{change}Imprimir comprovante?")) return;

        var data = await runner.RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(sale.SaleId));
        if (data is null) return;
        try
        {
            printer.Print(data);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao imprimir comprovante da venda {Number}", sale.Number);
            dialogs.ShowError($"A venda foi concluída, mas a impressão falhou: {ex.Message}\nUse F8 para reimprimir.");
        }
    }

    [RelayCommand]
    private async Task ReprintAsync()
    {
        var sale = await PickRecentSaleAsync("Reimprimir comprovante", onlyCompleted: false);
        if (sale is null) return;
        var data = await runner.RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(sale.Id));
        if (data is not null) dialogs.ShowReceipt(data);
    }

    [RelayCommand]
    private async Task CancelSaleAsync()
    {
        if (!EnsureCashOpen()) return;
        var sale = await PickRecentSaleAsync("Cancelar venda", onlyCompleted: true);
        if (sale is null) return;

        var reason = dialogs.Prompt("Cancelar venda", $"Venda nº {sale.Number} — {sale.Total:C}\nMotivo do cancelamento:");
        if (string.IsNullOrWhiteSpace(reason)) return;

        var authorizerId = await authorizer.AuthorizeAsync(Permissions.CancelSale, "O cancelamento");
        if (authorizerId is null) return;

        var result = await runner.RunAsync<CheckoutService, Result<Guid>>(c => c.CancelAsync(sale.Id, reason, authorizerId.Value));
        if (!result.Succeeded) { dialogs.ShowError(result.Error!); return; }

        StatusMessage = $"Venda nº {sale.Number} cancelada. Estoque e caixa estornados.";
        await RefreshCashAsync();
    }

    private async Task<SaleListItem?> PickRecentSaleAsync(string title, bool onlyCompleted)
    {
        var sales = await runner.RunAsync<SalesQueryService, IReadOnlyList<SaleListItem>>(q => q.ListRecentAsync(state.Terminal.Id));
        if (sales.Count == 0) { dialogs.ShowInfo("Nenhuma venda registrada neste terminal."); return null; }

        var rows = sales.Select(s => new SaleRow(s)).ToList();
        var picked = dialogs.Pick(title, rows,
            [new GridColumn("Nº", nameof(SaleRow.Number), Width: 80), new GridColumn("Data/hora", nameof(SaleRow.When), Width: 150),
             new GridColumn("Total", nameof(SaleRow.Total), "C", 110), new GridColumn("Situação", nameof(SaleRow.Situation), Width: 110),
             new GridColumn("Operador", nameof(SaleRow.Operator))],
            r => !onlyCompleted || r.Sale.Status == SaleStatus.Completed);
        return picked?.Sale;
    }

    public sealed record SaleRow(SaleListItem Sale)
    {
        public long Number => Sale.Number;
        public string When => Sale.CompletedAt?.ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture) ?? "";
        public decimal Total => Sale.Total;
        public string Situation => Sale.Status == SaleStatus.Cancelled ? "Cancelada" : "Concluída";
        public string Operator => Sale.OperatorName;
    }

    // ---------- Caixa ----------

    [RelayCommand]
    private async Task OpenCashAsync()
    {
        if (IsCashOpen) return;
        var value = dialogs.Prompt("Abertura de caixa", "Fundo de troco (R$):", "0,00");
        if (value is null) return;
        if (!TryParseDecimal(value, out var amount)) { dialogs.ShowError("Valor inválido."); return; }

        var result = await runner.RunAsync<CashService, Result<CashSessionSummary>>(c => c.OpenAsync(state.Terminal.Id, state.User!.UserId, amount));
        await ReportAsync(result, "Caixa aberto.");
    }

    [RelayCommand]
    private async Task SupplyAsync()
    {
        if (!EnsureCashOpen() || !TryPromptAmount("Suprimento", out var amount)) return;
        var reason = dialogs.Prompt("Suprimento", "Motivo (opcional):") ?? "";
        var userId = await authorizer.AuthorizeAsync(Permissions.ManageCash, "O suprimento");
        if (userId is null) return;
        var result = await runner.RunAsync<CashService, Result<CashSessionSummary>>(c =>
            c.SupplyAsync(state.Terminal.Id, userId.Value, amount, reason));
        await ReportAsync(result, "Suprimento registrado.");
    }

    [RelayCommand]
    private async Task WithdrawAsync()
    {
        if (!EnsureCashOpen() || !TryPromptAmount("Sangria", out var amount)) return;
        var reason = dialogs.Prompt("Sangria", "Motivo:");
        if (string.IsNullOrWhiteSpace(reason)) return;
        var userId = await authorizer.AuthorizeAsync(Permissions.ManageCash, "A sangria");
        if (userId is null) return;
        var result = await runner.RunAsync<CashService, Result<CashSessionSummary>>(c =>
            c.WithdrawAsync(state.Terminal.Id, userId.Value, amount, reason));
        await ReportAsync(result, "Sangria registrada.");
    }

    [RelayCommand]
    private async Task CloseCashAsync()
    {
        if (!EnsureCashOpen()) return;
        if (Items.Count > 0) { dialogs.ShowError("Conclua ou descarte a venda em andamento antes de fechar o caixa."); return; }
        if (!TryPromptAmount("Fechamento de caixa", out var counted, "Valor contado em dinheiro na gaveta (R$):", allowZero: true)) return;

        var result = await runner.RunAsync<CashService, Result<CashSessionSummary>>(c =>
            c.CloseAsync(state.Terminal.Id, state.User!.UserId, counted));
        if (!result.Succeeded) { dialogs.ShowError(result.Error!); return; }

        var s = result.Value!;
        var diff = s.Difference ?? 0;
        var situation = diff == 0 ? "Sem divergência." : diff < 0 ? $"FALTA de {-diff:C}." : $"SOBRA de {diff:C}.";
        dialogs.ShowInfo($"Caixa fechado.\n\nEsperado: {s.ExpectedCash:C}\nContado: {s.CountedAmount:C}\n{situation}");
        await RefreshCashAsync();
    }

    // ---------- Retaguarda ----------

    [RelayCommand]
    private async Task OpenAdminAsync()
    {
        if (!CanOpenAdmin) { dialogs.ShowError("Seu usuário não tem acesso à retaguarda."); return; }
        var vm = adminFactory();
        var window = new AdminWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
        await vm.LoadAsync();
        window.ShowDialog();
    }

    // ---------- Auxiliares ----------

    private async Task RefreshCashAsync()
    {
        var session = await runner.RunAsync<CashService, CashSessionSummary?>(c => c.GetOpenSessionAsync(state.Terminal.Id));
        IsCashOpen = session is not null;
        CashStatus = session is null
            ? "Caixa FECHADO"
            : $"Caixa aberto desde {session.OpenedAt.ToLocalTime():dd/MM HH:mm}  ·  Dinheiro em caixa: {session.ExpectedCash:C}";
    }

    private async Task ReportAsync(Result<CashSessionSummary> result, string success)
    {
        if (!result.Succeeded) { dialogs.ShowError(result.Error!); return; }
        StatusMessage = success;
        await RefreshCashAsync();
    }

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountTotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Paid));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(Change));
        OnPropertyChanged(nameof(ItemCount));
        FinalizeCommand.NotifyCanExecuteChanged();
    }

    private bool EnsureCartEditable()
    {
        if (!_outcomeUnknown) return true;
        dialogs.ShowError("A última finalização não foi confirmada. Pressione F10 para reenviar antes de alterar a venda.");
        return false;
    }

    private bool EnsureCashOpen()
    {
        if (IsCashOpen) return true;
        StatusMessage = "Abra o caixa (F2) para vender.";
        return false;
    }

    /// <summary>Lê o valor digitado; vazio significa "o valor restante".</summary>
    private bool TryReadAmount(out decimal amount)
    {
        if (string.IsNullOrWhiteSpace(AmountInput)) { amount = Remaining; return true; }
        if (TryParseDecimal(AmountInput, out amount) && amount > 0) return true;
        dialogs.ShowError("Valor inválido.");
        return false;
    }

    private bool TryPromptAmount(string title, out decimal amount, string message = "Valor (R$):", bool allowZero = false)
    {
        amount = 0;
        var value = dialogs.Prompt(title, message);
        if (value is null) return false;
        if (TryParseDecimal(value, out amount) && (amount > 0 || (allowZero && amount == 0))) return true;
        dialogs.ShowError("Valor inválido.");
        return false;
    }

    private void ResetCart(string message)
    {
        Items.Clear();
        Payments.Clear();
        LastItem = null;
        CustomerDocument = null;
        AmountInput = "";
        SelectedPayment = PaymentOptions[0];
        _clientRequestId = Guid.CreateVersion7();
        _discountAuthorizerId = null;
        StatusMessage = message;
    }

    private void Renumber()
    {
        var lines = Items.Select((l, i) => l.WithNumber(i + 1)).ToList();
        Items.Clear();
        foreach (var line in lines) Items.Add(line);
    }

    internal static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text.Replace("R$", "", StringComparison.Ordinal).Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);
}
