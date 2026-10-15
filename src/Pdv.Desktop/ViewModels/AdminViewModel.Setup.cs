using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Fiscal;
using Pdv.Application.Maintenance;

namespace Pdv.Desktop.ViewModels;

/// <summary>Retaguarda — assistente de implantação, tabela IBPT (Tributação) e inutilização de números (NFC-e).</summary>
public partial class AdminViewModel
{
    public bool ShowSetupTab => CanManageCompany || CanManageSystem;

    public ObservableCollection<SetupRow> SetupItems { get; } = [];

    [ObservableProperty] private string _setupSummary = "";
    [ObservableProperty] private bool _setupComplete;
    [ObservableProperty] private string _ibptStatus = "";

    /// <summary>A janela troca para a aba pedida (cabeçalho da aba).</summary>
    public event EventHandler<string>? NavigateRequested;

    private async Task LoadSetupAsync()
    {
        if (!ShowSetupTab) return;
        Apply(await Runner.RunAsync<ISetupAssistant, SetupChecklist>(s => s.GetAsync(ActorId, default)));
        if (CanManageCompany)
        {
            var t = await Runner.RunAsync<TaxApproximationService, TaxTableStatus>(s => s.GetStatusAsync(default));
            IbptStatus = t.Rows == 0
                ? "Tabela IBPT não importada: o \"valor aproximado dos tributos\" (Lei 12.741) não aparece nas notas."
                : $"Tabela IBPT {t.Version} ({t.Source}) · {t.Rows:N0} NCM · válida até {t.ValidTo:dd/MM/yyyy}" +
                  (t.Expired ? " — VENCIDA" : "") + (t.ProductsWithoutRate > 0 ? $" · {t.ProductsWithoutRate} produto(s) com NCM fora da tabela" : "");
        }
    }

    private void Apply(SetupChecklist checklist)
    {
        SetupItems.Clear();
        foreach (var item in checklist.Items) SetupItems.Add(new SetupRow(item));
        SetupComplete = checklist.Complete;
        SetupSummary = checklist.Complete
            ? "Loja pronta: todos os itens conferidos."
            : $"{checklist.Ready} de {checklist.Items.Count} itens prontos · {checklist.Pending} pendente(s) para emitir NFC-e com segurança.";
    }

    [RelayCommand]
    private async Task RefreshSetupAsync() => await LoadSetupAsync();

    [RelayCommand]
    private void OpenSetupItem(SetupRow? row)
    {
        if (row is not null) NavigateRequested?.Invoke(this, row.Item.Tab);
    }

    [RelayCommand]
    private async Task ConfirmTaxReviewAsync()
    {
        if (!Dialogs.Confirm("Confirma que o contador conferiu os perfis tributários (CFOP, CSOSN/CST, PIS/COFINS) e os NCM dos produtos?")) return;
        Apply(await Runner.RunAsync<ISetupAssistant, SetupChecklist>(s => s.ConfirmTaxReviewAsync(ActorId, default)));
    }

    [RelayCommand]
    private async Task TestSefazFromSetupAsync()
    {
        var result = await Runner.RunAsync<ISetupAssistant, Result<string>>(s => s.TestSefazAsync(ActorId, default));
        await LoadSetupAsync();
        if (result.Succeeded) Dialogs.ShowInfo(result.Value!);
        else Dialogs.ShowError(result.Error!);
    }

    // ---------- Tabela IBPT ----------

    [RelayCommand]
    private async Task ImportIbptAsync()
    {
        var file = Dialogs.PickFile("Tabela IBPT da sua UF (deolhonoimposto.ibpt.org.br)", "Tabela IBPT (*.csv)|*.csv");
        if (file is null) return;
        var content = TaxApproximationService.Decode(await File.ReadAllBytesAsync(file));
        var result = await Runner.RunAsync<TaxApproximationService, Result<TaxTableImport>>(s => s.ImportAsync(ActorId, content, default));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        var r = result.Value!;
        Dialogs.ShowInfo($"Tabela IBPT {r.Version} importada: {r.Rows:N0} NCM, válida até {r.ValidTo:dd/MM/yyyy}." +
            "\nAs próximas notas e comprovantes mostram o valor aproximado dos tributos.");
        await LoadSetupAsync();
    }

    // ---------- Inutilização ----------

    [RelayCommand]
    private async Task VoidNumbersAsync()
    {
        var suggestions = await Runner.RunAsync<NfceService, IReadOnlyList<long>>(s => s.GetVoidSuggestionsAsync(ActorId, default));
        var hint = suggestions.Count == 0
            ? "Nenhum número pendente encontrado (lacunas ou rejeitadas de vendas canceladas)."
            : $"Sugeridos: {string.Join(", ", suggestions.Take(15))}{(suggestions.Count > 15 ? "..." : "")}";
        var range = Dialogs.Prompt("Inutilizar numeração da NFC-e",
            $"{hint}\n\nNúmero ou faixa a inutilizar (ex.: 15 ou 15-18). Só números sem nota autorizada:",
            suggestions.Count > 0 ? suggestions[0].ToString(CultureInfo.InvariantCulture) : "");
        if (string.IsNullOrWhiteSpace(range)) return;
        var parts = range.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!long.TryParse(parts[0], out var from) || !long.TryParse(parts.Length > 1 ? parts[1] : parts[0], out var to) || to < from)
        {
            Dialogs.ShowError("Informe um número (15) ou uma faixa (15-18).");
            return;
        }
        var reason = Dialogs.Prompt("Inutilizar numeração da NFC-e", "Justificativa (mínimo 15 caracteres, vai para a SEFAZ):",
            "Numeracao nao utilizada por falha na emissao");
        if (string.IsNullOrWhiteSpace(reason)) return;
        if (!Dialogs.Confirm($"Inutilizar {(to > from ? $"os números {from} a {to}" : $"o número {from}")} na SEFAZ? Não dá para desfazer.")) return;

        var result = await Runner.RunAsync<NfceService, Result<string>>(s => s.VoidNumbersAsync(ActorId, from, to, reason, default));
        await LoadNfceDocumentsAsync();
        if (result.Succeeded) Dialogs.ShowInfo(result.Value!);
        else Dialogs.ShowError(result.Error!);
    }

    public sealed record SetupRow(SetupItem Item)
    {
        public string Title => Item.Title;
        public string Detail => Item.Detail;
        public string Tab => Item.Tab;
        public bool IsOk => Item.State == SetupState.Ok;
        public bool IsWarning => Item.State == SetupState.Warning;
        public bool IsPending => Item.State == SetupState.Pending;
        public string StateText => Item.State switch
        {
            SetupState.Ok => "Pronto",
            SetupState.Warning => "Atenção",
            _ => "Pendente",
        };
    }
}
