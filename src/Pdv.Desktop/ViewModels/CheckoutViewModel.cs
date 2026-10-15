using Pdv.Client;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Payments;
using Pdv.Application.Reports;
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

/// <param name="Tef">Cartão aprovado pelo TEF deste caixa (pendente de confirmação até a venda ser gravada).</param>
/// <param name="PixChargeId">Pix com QR dinâmico confirmado pelo PSP.</param>
public sealed record PaymentEntry(PaymentMethod Method, string Label, decimal Amount, string? Reference = null,
    TefApproval? Tef = null, Guid? PixChargeId = null)
{
    public CheckoutPayment ToCommand() => new(Method, Amount, Reference,
        Tef is { } t ? new PaymentIntegration(t.Nsu, t.AuthorizationCode, t.CardBrand, t.AcquirerCnpj, t.AcquirerName) : null,
        PixChargeId);
}

/// <summary>Equipamentos de pagamento deste caixa (TEF configurado em Pdv:Tef:Provider; nulo = maquininha não integrada).</summary>
public sealed record PaymentDevices(TefCoordinator? Tef);

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
    ServerRunner runner,
    AppState state,
    IDialogService dialogs,
    SupervisorAuthorizer authorizer,
    ReceiptPrinter printer,
    Func<AdminViewModel> adminFactory,
    SaleJournal journal,
    PaymentDevices devices,
    RawPrinter rawPrinter,
    TicketPrinter tickets,
    Func<LoginViewModel> loginFactory) : ObservableObject
{
    // Chave de idempotência do carrinho atual. Só muda depois de uma venda concluída ou carrinho descartado,
    // então reenviar após timeout/erro de rede nunca duplica a venda.
    private Guid _clientRequestId = Guid.CreateVersion7();

    // Após falha de comunicação não sabemos se a venda foi gravada: o carrinho fica travado até reenviar.
    private bool _outcomeUnknown;

    // Quem liberou os descontos desta venda (o próprio operador, se tiver permissão).
    private Guid? _discountAuthorizerId;

    // Pix com QR dinâmico configurado no servidor (consultado ao abrir e ao voltar da retaguarda).
    private bool _pixIntegrated;

    public ObservableCollection<CartLine> Items { get; } = [];

    public ObservableCollection<PaymentEntry> Payments { get; } = [];

    public IReadOnlyList<PaymentOption> PaymentOptions { get; } =
    [
        new(PaymentMethod.Cash, PaymentLabels.For(PaymentMethod.Cash)),
        new(PaymentMethod.Pix, PaymentLabels.For(PaymentMethod.Pix)),
        new(PaymentMethod.DebitCard, PaymentLabels.For(PaymentMethod.DebitCard)),
        new(PaymentMethod.CreditCard, PaymentLabels.For(PaymentMethod.CreditCard)),
        new(PaymentMethod.ExchangeVoucher, PaymentLabels.For(PaymentMethod.ExchangeVoucher)),
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
        (u.Can(Permissions.ManageProducts) || u.Can(Permissions.AdjustStock) || u.Can(Permissions.ManageUsers)
         || u.Can(Permissions.ViewReports) || u.Can(Permissions.ManageCompany) || u.Can(Permissions.ManageSystem));

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
        await RecoverInterruptedSaleAsync();
        await UndoLeftoverCardPaymentsAsync();
        await RefreshPaymentOptionsAsync();
        var license = await runner.RunAsync<Pdv.Application.Licensing.LicenseService, Pdv.Application.Licensing.LicenseStatus>(s => s.GetStatusAsync(null, default));
        if (license.Warning is { } warning) dialogs.ShowInfo(warning);
        if (!IsCashOpen && license.CanSell) await OpenCashAsync();
    }

    private async Task RefreshPaymentOptionsAsync() =>
        _pixIntegrated = await runner.RunAsync<PixService, bool>(s => s.IsEnabledAsync(default));

    /// <summary>
    /// Cartões aprovados no TEF cuja venda não chegou a ser concluída (caixa desligado com o carrinho aberto):
    /// são desfeitos, para o cliente não ser cobrado sem venda.
    /// </summary>
    private async Task UndoLeftoverCardPaymentsAsync()
    {
        if (devices.Tef is not { } tef || tef.Pending.Count == 0) return;
        var pending = tef.Pending;
        try
        {
            await tef.UndoAsync(pending, CancellationToken.None);
            Log.Warning("{Count} transação(ões) de cartão sem venda desfeita(s): {Nsus}", pending.Count, string.Join(", ", pending.Select(p => p.Nsu)));
            dialogs.ShowInfo($"{pending.Count} pagamento(s) com cartão de uma venda não concluída foram desfeitos no TEF " +
                $"({string.Join(", ", pending.Select(p => $"{p.Amount:C} NSU {p.Nsu}"))}). O cliente não será cobrado.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao desfazer transações pendentes do TEF");
            dialogs.ShowError($"Há pagamentos com cartão pendentes no TEF que não puderam ser desfeitos: {ex.Message}");
        }
    }

    /// <summary>
    /// O caixa desligou (queda de energia, travamento) enquanto concluía uma venda: o diário local diz qual era,
    /// e o servidor diz, pela chave de idempotência, se ela chegou a ser gravada. Nada é duplicado nem perdido.
    /// </summary>
    private async Task RecoverInterruptedSaleAsync()
    {
        var pending = journal.Load();
        if (pending is null) return;

        var cards = (devices.Tef?.Pending ?? []).Where(a => pending.Payments.Any(p => p.Integration?.TransactionId == a.Nsu)).ToList();
        var found = await runner.RunAsync<SalesQueryService, SaleListItem?>(q => q.FindByRequestAsync(pending.ClientRequestId, default));
        if (found is not null)
        {
            await SettleCardsAsync(cards, confirm: true);
            journal.Clear();
            Log.Information("Venda interrompida {RequestId} já estava gravada como nº {Number}", pending.ClientRequestId, found.Number);
            StatusMessage = $"Venda nº {found.Number} (interrompida) já estava gravada.";
            dialogs.ShowInfo($"O caixa foi interrompido enquanto concluía uma venda.\n\nA venda nº {found.Number} ({found.Total:C}) FOI GRAVADA no servidor — " +
                "não é preciso registrar de novo. Use F8 para reimprimir o comprovante.");
            return;
        }

        var paid = pending.Payments.Sum(p => p.Amount);
        if (!dialogs.Confirm($"O caixa foi interrompido enquanto concluía uma venda que NÃO chegou ao servidor " +
            $"({pending.Items.Count} item(ns), pagamentos de {paid:C}).\n\nConcluir essa venda agora?\n" +
            "(Não = descartar: nem a mercadoria nem o dinheiro foram registrados.)"))
        {
            await SettleCardsAsync(cards, confirm: false);
            journal.Clear();
            Log.Warning("Venda interrompida {RequestId} descartada pelo operador", pending.ClientRequestId);
            return;
        }

        var result = await runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(pending, default));
        await SettleCardsAsync(cards, confirm: result.Succeeded);
        journal.Clear();
        if (!result.Succeeded)
        {
            dialogs.ShowError($"Não foi possível concluir a venda interrompida:\n{result.Error}\n\nRegistre a venda de novo.");
            return;
        }
        Log.Information("Venda interrompida {RequestId} concluída como nº {Number}", pending.ClientRequestId, result.Value!.Number);
        StatusMessage = $"Venda nº {result.Value.Number} (interrompida) concluída.";
        await RefreshCashAsync();
        var fiscal = await IssueNfceAsync(result.Value);
        await PrintAfterSaleAsync(result.Value, fiscal);
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

        var product = await runner.RunAsync<ProductLookupService, ProductLookupItem?>(lookup => lookup.FindByBarcodeAsync(input, default));
        IReadOnlyList<ProductLookupItem> matches = product is not null ? []
            : await runner.RunAsync<ProductLookupService, IReadOnlyList<ProductLookupItem>>(lookup => lookup.SearchAsync(input, 30, default));

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

    /// <summary>Altera a quantidade do item selecionado (o desconto do item é mantido se ainda couber).</summary>
    [RelayCommand]
    private void ChangeQuantity()
    {
        var line = SelectedItem ?? Items.LastOrDefault();
        if (line is null || !EnsureCartEditable()) return;
        var input = dialogs.Prompt("Alterar quantidade", $"{line.Product.Name}\nNova quantidade ({line.Product.Unit}):",
            line.Quantity.ToString("0.###", CultureInfo.CurrentCulture));
        if (input is null) return;
        if (!TryParseDecimal(input, out var quantity) || quantity <= 0) { dialogs.ShowError("Quantidade inválida."); return; }

        var index = Items.IndexOf(line);
        var updated = new CartLine(line.Number, line.Product, quantity);
        updated.Discount = line.Discount <= updated.GrossTotal ? line.Discount : 0;
        Items[index] = updated;
        SelectedItem = updated;
        LastItem = updated;
        Payments.Clear();
        StatusMessage = $"{line.Product.Name}: quantidade alterada para {quantity:0.###}.";
    }

    /// <summary>Consulta de preço e estoque sem lançar na venda.</summary>
    [RelayCommand]
    private async Task PriceCheckAsync()
    {
        var term = dialogs.Prompt("Consulta de preço", "Código de barras ou parte do nome:");
        if (string.IsNullOrWhiteSpace(term)) return;
        var found = await runner.RunAsync<ProductLookupService, IReadOnlyList<PriceCheckRow>>(s => s.PriceCheckAsync(term));
        if (found.Count == 0) { dialogs.ShowError("Nenhum produto encontrado."); return; }
        if (found.Count == 1)
        {
            var (p, stock) = found[0];
            dialogs.ShowInfo($"{p.Name}\n\nPreço: {p.SalePrice:C} / {p.Unit}\nEstoque: {stock:0.###} {p.Unit}");
            return;
        }
        dialogs.ShowGrid("Consulta de preço", found.Select(f => new PriceRow(f.Product.Name, f.Product.Unit, f.Product.SalePrice, f.Stock)).ToList(),
            [new GridColumn("Produto", nameof(PriceRow.Name)), new GridColumn("Un", nameof(PriceRow.Unit), Width: 50),
             new GridColumn("Preço", nameof(PriceRow.Price), "C", 110), new GridColumn("Estoque", nameof(PriceRow.Stock), "0.###", 90)]);
    }

    public sealed record PriceRow(string Name, string Unit, decimal Price, decimal Stock);

    /// <summary>Devolução/troca de itens de uma venda: dinheiro da gaveta ou vale-troca (com supervisor).</summary>
    [RelayCommand]
    private async Task ReturnItemsAsync()
    {
        if (!EnsureCashOpen()) return;
        var input = dialogs.Prompt("Devolução / troca", "Número da venda (está no comprovante):");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!long.TryParse(input.Trim().TrimStart('#'), out var number)) { dialogs.ShowError("Número de venda inválido."); return; }

        var sale = await runner.RunAsync<ReturnService, Result<ReturnableSale>>(s => s.GetReturnableAsync(number));
        if (!sale.Succeeded) { dialogs.ShowError(sale.Error!); return; }
        var form = new ReturnForm(sale.Value!);
        if (form.Lines.Count == 0) { dialogs.ShowInfo("Todos os itens desta venda já foram devolvidos."); return; }

        var dialog = new ReturnDialog(form) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var authorizerId = await authorizer.AuthorizeAsync(Permissions.CancelSale, "A devolução");
        if (authorizerId is null) return;

        var result = await runner.RunAsync<ReturnService, Result<ReturnReceipt>>(s => s.CreateAsync(new CreateReturnCommand(
            form.Sale.SaleId, dialog.Quantities, form.Method, form.Reason, authorizerId.Value, state.Terminal.Id)));
        if (!result.Succeeded) { dialogs.ShowError(result.Error!); return; }

        var r = result.Value!;
        await RefreshCashAsync();
        if (r.Method == RefundMethod.Cash)
        {
            StatusMessage = $"Devolução da venda nº {r.SaleNumber}: entregue {r.Total:C} em dinheiro.";
            dialogs.ShowInfo($"Devolução registrada.\n\nEntregue ao cliente: {r.Total:C} em dinheiro.\nO estoque foi atualizado.");
            return;
        }

        StatusMessage = $"Vale-troca {r.VoucherCode} de {r.Total:C} gerado (venda nº {r.SaleNumber}).";
        if (dialogs.Confirm($"Vale-troca gerado: {r.VoucherCode}\nValor: {r.Total:C} · válido até {r.VoucherExpiresAt?.ToLocalTime():dd/MM/yyyy}\n\nImprimir o vale?"))
        {
            try { tickets.PrintVoucher(r, await CompanyNameAsync()); }
            catch (Exception ex) { Log.Error(ex, "Falha ao imprimir vale-troca"); dialogs.ShowError($"Não foi possível imprimir: {ex.Message}"); }
        }
    }

    /// <summary>Troca de operador / bloqueio: só com a venda vazia; o caixa continua aberto.</summary>
    [RelayCommand]
    private async Task SwitchOperatorAsync()
    {
        if (Items.Count > 0) { dialogs.ShowError("Conclua ou descarte a venda em andamento antes de trocar de operador."); return; }
        var previous = state.User;
        while (true)
        {
            var vm = loginFactory();
            var window = new LoginWindow(vm) { Owner = System.Windows.Application.Current.MainWindow, Title = "Sistema PDV — Caixa bloqueado" };
            if (window.ShowDialog() == true) break;
            if (dialogs.Confirm("Fechar o sistema? (o caixa continua aberto para o próximo operador)"))
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }
        }
        Log.Information("Operador trocado: {Old} → {New}", previous?.Username, state.User?.Username);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(OperatorName));
        OnPropertyChanged(nameof(CanOpenAdmin));
        StatusMessage = $"Operador: {state.User?.DisplayName}.";
        await RefreshCashAsync();
    }

    private async Task<string> CompanyNameAsync() =>
        (await runner.RunAsync<CompanySettingsService, CompanySettingsModel>(s => s.GetAsync())) is { } c ? c.TradeName ?? c.LegalName : "";

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
    private async Task ClearCartAsync()
    {
        if (Items.Count == 0 || !EnsureCartEditable()) return;
        var pix = Payments.Where(p => p.PixChargeId is not null).Sum(p => p.Amount);
        if (!dialogs.Confirm("Descartar a venda em andamento?" + (pix > 0
            ? $"\n\nAtenção: {pix:C} em Pix já foram recebidos e precisarão ser devolvidos em Retaguarda › Pagamentos." : ""))) return;
        await SettleCardsAsync(Payments.Select(p => p.Tef).OfType<TefApproval>().ToList(), confirm: false);
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

        if (SelectedPayment.Method == PaymentMethod.ExchangeVoucher)
        {
            await AddVoucherPaymentAsync(string.IsNullOrWhiteSpace(AmountInput) ? null : amount);
            return;
        }

        var integrated = (SelectedPayment.Method is PaymentMethod.CreditCard or PaymentMethod.DebitCard && devices.Tef is not null)
            || (SelectedPayment.Method == PaymentMethod.Pix && _pixIntegrated);
        if (integrated && amount > 0)
        {
            if (amount > Remaining) { dialogs.ShowError("Cartão e Pix não podem passar do valor restante (não há troco)."); return; }
            var entry = SelectedPayment.Method == PaymentMethod.Pix
                ? PayWithPix(amount)
                : PayWithCard(devices.Tef!, SelectedPayment, amount);
            if (entry is null) return;
            Payments.Add(entry);
            AmountInput = "";
            if (Remaining == 0) await FinalizeAsync();
            else StatusMessage = $"{entry.Label}: {amount:C} aprovado. Restam {Remaining:C}.";
            return;
        }

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

    /// <summary>Vale-troca: pede o código, confere o saldo e lança até o valor que falta.</summary>
    private PaymentEntry? PayWithPix(decimal amount)
    {
        var dialog = new PixPaymentDialog(runner, state.Terminal.Id, _clientRequestId, amount) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Charge is not { } charge) return null;
        Log.Information("Pix {Txid} confirmado: {Amount} ({E2E})", charge.Txid, charge.Amount, charge.EndToEndId);
        return new PaymentEntry(PaymentMethod.Pix, "Pix (QR dinâmico)", charge.Amount, charge.Txid, PixChargeId: charge.Id);
    }

    private PaymentEntry? PayWithCard(TefCoordinator tef, PaymentOption option, decimal amount)
    {
        var type = option.Method == PaymentMethod.CreditCard ? TefCardType.Credit : TefCardType.Debit;
        var dialog = new TefPaymentDialog(tef, new TefRequest(type, amount, 1, $"{state.Terminal.Code}-{_clientRequestId:N}"))
        { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Outcome?.Approval is not { } approval) return null;
        Log.Information("Cartão aprovado no TEF: {Amount} NSU {Nsu} {Brand}", approval.Amount, approval.Nsu, approval.CardBrandName);
        return new PaymentEntry(option.Method, $"{option.Label} {approval.CardBrandName} NSU {approval.Nsu}", amount, Tef: approval);
    }

    /// <summary>Confirma (venda gravada) ou desfaz (venda não concluída) os cartões aprovados no TEF.</summary>
    private async Task SettleCardsAsync(List<TefApproval> cards, bool confirm)
    {
        if (cards.Count == 0 || devices.Tef is not { } tef) return;
        try
        {
            if (confirm) await tef.ConfirmAsync(cards, CancellationToken.None);
            else await tef.UndoAsync(cards, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao {Action} transações do TEF", confirm ? "confirmar" : "desfazer");
            dialogs.ShowError($"O TEF não {(confirm ? "confirmou" : "desfez")} o pagamento com cartão: {ex.Message}\n" +
                "A transação fica pendente e será resolvida na próxima abertura do caixa.");
        }
    }

    private async Task AddVoucherPaymentAsync(decimal? requested)
    {
        var code = dialogs.Prompt("Vale-troca", "Código do vale-troca:");
        if (string.IsNullOrWhiteSpace(code)) return;
        var voucher = await runner.RunAsync<ReturnService, VoucherInfo?>(s => s.GetVoucherAsync(code));
        if (voucher is null) { dialogs.ShowError("Vale-troca não encontrado. Confira o código."); return; }
        if (voucher.ExpiresAt < DateTimeOffset.Now) { dialogs.ShowError($"Vale-troca vencido em {voucher.ExpiresAt.ToLocalTime():dd/MM/yyyy}."); return; }
        var alreadyUsed = Payments.Where(p => p.Reference == voucher.Code).Sum(p => p.Amount);
        var available = voucher.Balance - alreadyUsed;
        if (available <= 0) { dialogs.ShowError($"O vale {voucher.Code} não tem saldo."); return; }

        var amount = requested ?? Math.Min(Remaining, available);
        if (amount > available) { dialogs.ShowError($"Saldo do vale: {available:C}."); return; }
        if (amount > Remaining) { dialogs.ShowError("O vale-troca não gera troco: lance no máximo o valor que falta."); return; }

        Payments.Add(new PaymentEntry(PaymentMethod.ExchangeVoucher, $"Vale-troca {voucher.Code}", amount, voucher.Code));
        AmountInput = "";
        if (Remaining == 0) await FinalizeAsync();
        else StatusMessage = $"Vale {voucher.Code}: {amount:C} lançado. Restam {Remaining:C}.";
    }

    [RelayCommand]
    private async Task RemovePaymentAsync(PaymentEntry? payment)
    {
        if (payment is null || !EnsureCartEditable()) return;
        if (payment.PixChargeId is not null && !dialogs.Confirm($"Este Pix de {payment.Amount:C} já foi recebido.\n\n" +
            "Se removê-lo, o valor precisa ser devolvido ao cliente em Retaguarda › Pagamentos. Remover?")) return;
        if (payment.Tef is { } card) await SettleCardsAsync([card], confirm: false);
        Payments.Remove(payment);
    }

    [RelayCommand(CanExecute = nameof(CanFinalize))]
    private async Task FinalizeAsync()
    {
        if (!EnsureCashOpen()) return;

        // Sem pagamentos lançados: usa a forma selecionada com o valor digitado (ou o total).
        if (Payments.Count == 0 && SelectedPayment?.Method == PaymentMethod.ExchangeVoucher)
        {
            await AddPaymentAsync();
            return;
        }
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
            Payments.Select(p => p.ToCommand()).ToList(),
            DiscountTotal > 0 ? _discountAuthorizerId : null,
            CustomerDocument);

        IsBusy = true;
        SaleReceipt receipt;
        // Gravado no disco antes de enviar: se o caixa desligar agora, na volta o sistema confere no servidor.
        journal.Save(command);
        try
        {
            var result = await runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(command, default));
            journal.Clear(); // o servidor respondeu: a venda foi gravada ou recusada, não está mais "no ar"
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
            dialogs.ShowError($"Não foi possível confirmar a venda com o servidor da loja.\n{ex.Message}\n\n" +
                "Tente novamente com F10: a venda não será duplicada.");
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Log.Information("Venda {Number} ({SaleId}) concluída: {Total} (repetida: {Repeated})",
            receipt.Number, receipt.SaleId, receipt.Total, receipt.AlreadyProcessed);
        var cards = Payments.Select(p => p.Tef).OfType<TefApproval>().ToList();
        if (Payments.Any(p => p.Method == PaymentMethod.Cash)) rawPrinter.OpenDrawer();
        await SettleCardsAsync(cards, confirm: true);
        ResetCart(receipt.Change > 0 ? $"Venda nº {receipt.Number} concluída. TROCO: {receipt.Change:C}" : $"Venda nº {receipt.Number} concluída.");
        await RefreshCashAsync();
        var fiscal = await IssueNfceAsync(receipt);
        if (await PrintAfterSaleAsync(receipt, fiscal)) PrintCardReceipts(cards);
    }

    /// <summary>Via do cliente dos cartões, junto com o comprovante da venda.</summary>
    private void PrintCardReceipts(IEnumerable<TefApproval> cards)
    {
        foreach (var card in cards.Where(c => !string.IsNullOrWhiteSpace(c.CustomerReceipt)))
        {
            try { tickets.PrintCardReceipt(card.CustomerReceipt!); }
            catch (Exception ex) { Log.Error(ex, "Falha ao imprimir comprovante do cartão NSU {Nsu}", card.Nsu); }
        }
    }

    /// <summary>
    /// Emite a NFC-e da venda recém-concluída. A venda já está gravada: falha fiscal nunca desfaz a venda,
    /// apenas avisa (rejeição) ou deixa a nota na fila (contingência / sem resposta).
    /// </summary>
    private async Task<NfceInfo?> IssueNfceAsync(SaleReceipt sale)
    {
        if (!await runner.RunAsync<NfceConfigurationService, bool>(c => c.IsEnabledAsync(default))) return null;
        Result<NfceInfo> result;
        try
        {
            result = await runner.RunAsync<NfceService, Result<NfceInfo>>(s => s.IssueForSaleAsync(sale.SaleId));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha inesperada ao emitir NFC-e da venda {Number}", sale.Number);
            dialogs.ShowError($"A venda foi concluída, mas a NFC-e não pôde ser emitida: {ex.Message}\nReenvie em Retaguarda › NFC-e.");
            return null;
        }

        if (!result.Succeeded)
        {
            dialogs.ShowError($"A venda foi concluída, mas a NFC-e não foi emitida:\n{result.Error}");
            return null;
        }

        var nfce = result.Value!;
        Log.Information("NFC-e {Number} da venda {Sale}: {Status} {Code} {Message}", nfce.Number, sale.Number, nfce.Status,
            nfce.StatusCode, nfce.StatusMessage);
        StatusMessage += nfce.Status switch
        {
            FiscalDocumentStatus.Authorized => $"  ·  NFC-e nº {nfce.Number} autorizada.",
            FiscalDocumentStatus.Contingency => $"  ·  NFC-e nº {nfce.Number} em contingência (será transmitida).",
            FiscalDocumentStatus.Unknown => $"  ·  NFC-e nº {nfce.Number} sem resposta da SEFAZ (será consultada).",
            _ => $"  ·  NFC-e nº {nfce.Number}: {nfce.Status}.",
        };
        if (nfce.Status is FiscalDocumentStatus.Rejected or FiscalDocumentStatus.Denied)
            dialogs.ShowError($"NFC-e nº {nfce.Number} {(nfce.Status == FiscalDocumentStatus.Denied ? "denegada" : "rejeitada")} pela SEFAZ:\n" +
                $"{nfce.StatusCode} - {nfce.StatusMessage}\n\nA venda está gravada. Corrija o cadastro e reenvie em Retaguarda › NFC-e.");

        // SEFAZ respondendo: aproveita para transmitir notas antigas que ficaram na fila (sem travar o caixa).
        if (nfce.Status == FiscalDocumentStatus.Authorized) _ = TransmitPendingInBackgroundAsync();
        return nfce;
    }

    private async Task TransmitPendingInBackgroundAsync()
    {
        try
        {
            var sent = await Task.Run(() => runner.RunAsync<NfceService, int>(s => s.ProcessPendingAsync()));
            if (sent > 0) Log.Information("{Count} NFC-e pendente(s) autorizada(s) em segundo plano", sent);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao transmitir NFC-e pendentes em segundo plano");
        }
    }

    private bool CanFinalize() => !IsBusy && Items.Count > 0;

    // ---------- Comprovante e vendas gravadas ----------

    /// <returns>Verdadeiro se o comprovante foi impresso.</returns>
    private async Task<bool> PrintAfterSaleAsync(SaleReceipt sale, NfceInfo? fiscal)
    {
        var mode = printer.Options.Mode;
        if (mode == ReceiptPrintMode.Never) return false;
        var change = sale.Change > 0 ? $"TROCO: {sale.Change:C}\n\n" : "";
        var what = fiscal is { Status: FiscalDocumentStatus.Authorized or FiscalDocumentStatus.Contingency } ? "o DANFE da NFC-e" : "o comprovante";
        if (mode == ReceiptPrintMode.Ask && !dialogs.Confirm($"{change}Imprimir {what}?")) return false;

        var data = await runner.RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(sale.SaleId));
        if (data is null) return false;
        try
        {
            printer.Print(data, fiscal);
            rawPrinter.Cut();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao imprimir comprovante da venda {Number}", sale.Number);
            dialogs.ShowError($"A venda foi concluída, mas a impressão falhou: {ex.Message}\nUse F8 para reimprimir.");
            return false;
        }
    }

    [RelayCommand]
    private async Task ReprintAsync()
    {
        var sale = await PickRecentSaleAsync("Reimprimir comprovante", onlyCompleted: false);
        if (sale is null) return;
        var data = await runner.RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(sale.Id));
        var fiscal = await runner.RunAsync<NfceService, NfceInfo?>(s => s.GetBySaleAsync(sale.Id));
        if (data is not null) dialogs.ShowReceipt(data, fiscal);
    }

    [RelayCommand]
    private async Task CancelSaleAsync()
    {
        if (!EnsureCashOpen()) return;
        var sale = await PickRecentSaleAsync("Cancelar venda", onlyCompleted: true);
        if (sale is null) return;

        // Venda com NFC-e: a nota é cancelada na SEFAZ primeiro; se a SEFAZ recusar, a venda continua válida.
        var fiscal = await runner.RunAsync<NfceService, NfceInfo?>(s => s.GetBySaleAsync(sale.Id));
        if (fiscal is { Status: FiscalDocumentStatus.Pending or FiscalDocumentStatus.Unknown or FiscalDocumentStatus.Contingency })
        {
            dialogs.ShowError($"A NFC-e nº {fiscal.Number} desta venda ainda não foi autorizada.\nTransmita as pendentes (Retaguarda › NFC-e) antes de cancelar.");
            return;
        }
        var hasAuthorizedNfce = fiscal is { Status: FiscalDocumentStatus.Authorized };

        var reason = dialogs.Prompt("Cancelar venda", $"Venda nº {sale.Number} — {sale.Total:C}\nMotivo do cancelamento" +
            (hasAuthorizedNfce ? " (vai para a SEFAZ, mínimo 15 caracteres):" : ":"));
        if (string.IsNullOrWhiteSpace(reason)) return;
        if (hasAuthorizedNfce && reason.Trim().Length < 15) { dialogs.ShowError("Para cancelar a NFC-e o motivo precisa ter pelo menos 15 caracteres."); return; }

        var authorizerId = await authorizer.AuthorizeAsync(Permissions.CancelSale, "O cancelamento");
        if (authorizerId is null) return;

        if (hasAuthorizedNfce)
        {
            var nfce = await runner.RunAsync<NfceService, Result<NfceInfo>>(s => s.CancelAsync(sale.Id, reason, authorizerId.Value));
            if (!nfce.Succeeded) { dialogs.ShowError($"A NFC-e não foi cancelada, então a venda foi mantida:\n{nfce.Error}"); return; }
        }

        var result = await runner.RunAsync<CheckoutService, Result<Guid>>(c => c.CancelAsync(sale.Id, reason, authorizerId.Value));
        if (!result.Succeeded) { dialogs.ShowError(result.Error!); return; }

        StatusMessage = $"Venda nº {sale.Number} cancelada{(hasAuthorizedNfce ? " (NFC-e cancelada na SEFAZ)" : "")}. Estoque e caixa estornados.";
        await RefreshCashAsync();
        await RefundIntegratedPaymentsAsync(sale.Id);
    }

    /// <summary>Venda cancelada paga com cartão no TEF ou Pix dinâmico: o dinheiro volta ao cliente pelo mesmo meio.</summary>
    private async Task RefundIntegratedPaymentsAsync(Guid saleId)
    {
        var receipt = await runner.RunAsync<SalesQueryService, ReceiptData?>(q => q.GetReceiptAsync(saleId, default));
        if (receipt is null) return;
        foreach (var p in receipt.Payments.Where(p => p.IsIntegrated && p.TransactionId is not null))
        {
            if (p.Method == PaymentMethod.Pix)
            {
                dialogs.ShowInfo($"Esta venda tinha {p.Amount:C} em Pix. Faça a devolução ao cliente em Retaguarda › Pagamentos.");
                continue;
            }
            if (devices.Tef is not { } tef)
            {
                dialogs.ShowInfo($"Estorne {p.Amount:C} no cartão (NSU {p.TransactionId}) pelo terminal da credenciadora.");
                continue;
            }
            if (!dialogs.Confirm($"Estornar {p.Amount:C} no cartão do cliente agora (NSU {p.TransactionId})?")) continue;
            var original = new TefApproval(p.Method == PaymentMethod.CreditCard ? TefCardType.Credit : TefCardType.Debit, p.Amount,
                p.TransactionId!, p.AuthorizationCode, p.CardBrand, null, null, p.AcquirerName, null, receipt.CompletedAt, null, null);
            try
            {
                var outcome = await tef.ReverseAsync(original, new Progress<string>(m => StatusMessage = m), CancellationToken.None);
                Log.Information("Estorno TEF NSU {Nsu}: {Message}", p.TransactionId, outcome.Message);
                if (outcome.Approved) dialogs.ShowInfo(outcome.Message);
                else dialogs.ShowError($"Estorno não realizado: {outcome.Message}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Falha no estorno TEF NSU {Nsu}", p.TransactionId);
                dialogs.ShowError($"Estorno não realizado: {ex.Message}");
            }
        }
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
        if (result.Succeeded) rawPrinter.OpenDrawer();
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
        if (result.Succeeded) rawPrinter.OpenDrawer();
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
        await RefreshCashAsync();
        if (dialogs.Confirm($"Caixa fechado.\n\nEsperado: {s.ExpectedCash:C}\nContado: {s.CountedAmount:C}\n{situation}\n\nImprimir o relatório de fechamento?"))
        {
            try
            {
                var report = await runner.RunAsync<ReportService, CashClosingReport?>(r => r.CashClosingAsync(s.SessionId));
                if (report is not null) tickets.PrintCashClosing(report, await CompanyNameAsync());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Falha ao imprimir fechamento de caixa");
                dialogs.ShowError($"Não foi possível imprimir: {ex.Message}\nReimprima em Retaguarda › Relatórios › Caixas.");
            }
        }
    }

    // ---------- Retaguarda ----------

    [RelayCommand]
    private async Task OpenAdminAsync()
    {
        if (!CanOpenAdmin) { dialogs.ShowError("Seu usuário não tem acesso à retaguarda."); return; }
        var vm = adminFactory();
        vm.AttachTicketPrinter(tickets);
        vm.AttachPaymentDevices(devices);
        var window = new AdminWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
        await vm.LoadAsync();
        window.ShowDialog();
        await RefreshPaymentOptionsAsync(); // o Pix pode ter sido configurado agora
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
