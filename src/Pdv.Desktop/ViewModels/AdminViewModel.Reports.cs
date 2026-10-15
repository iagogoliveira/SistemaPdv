using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application.Fiscal;
using Pdv.Application.Reports;
using Pdv.Desktop.Services;
using Pdv.Desktop.Views;
using Pdv.Domain.Cash;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;

namespace Pdv.Desktop.ViewModels;

public enum ReportKind
{
    SalesByDay,
    Payments,
    TopProducts,
    CashSessions,
    Divergences,
    LowStock,
}

/// <summary>Retaguarda — aba Relatórios.</summary>
public partial class AdminViewModel
{
    public bool CanViewReports => state.User!.Can(Permissions.ViewReports);

    public IReadOnlyList<Choice<ReportKind>> ReportKinds { get; } =
    [
        new(ReportKind.SalesByDay, "Vendas por dia"),
        new(ReportKind.Payments, "Vendas por forma de pagamento"),
        new(ReportKind.TopProducts, "Produtos mais vendidos e margem"),
        new(ReportKind.CashSessions, "Caixas (aberturas e fechamentos)"),
        new(ReportKind.Divergences, "Divergências de caixa por operador"),
        new(ReportKind.LowStock, "Estoque abaixo do mínimo"),
    ];

    [ObservableProperty] private Choice<ReportKind>? _selectedReport;
    [ObservableProperty] private DateTime? _reportFrom;
    [ObservableProperty] private DateTime? _reportTo;
    [ObservableProperty] private IReadOnlyList<object> _reportRows = [];
    [ObservableProperty] private IReadOnlyList<GridColumn> _reportColumns = [];
    [ObservableProperty] private string _reportSummary = "";
    [ObservableProperty] private object? _selectedReportRow;

    public bool IsCashReport => SelectedReport?.Value == ReportKind.CashSessions;

    private TicketPrinter? _tickets;

    public void AttachTicketPrinter(TicketPrinter tickets) => _tickets = tickets;

    private void LoadReports()
    {
        if (!CanViewReports) return;
        var today = DateTime.Today;
        ReportFrom = new DateTime(today.Year, today.Month, 1);
        ReportTo = today;
        SelectedReport = ReportKinds[0];
    }

    partial void OnSelectedReportChanged(Choice<ReportKind>? value)
    {
        OnPropertyChanged(nameof(IsCashReport));
        RunReportCommand.Execute(null);
    }

    private ReportPeriod Period => new(DateOnly.FromDateTime(ReportFrom ?? DateTime.Today), DateOnly.FromDateTime(ReportTo ?? DateTime.Today));

    [RelayCommand]
    private async Task RunReportAsync()
    {
        if (!CanViewReports || SelectedReport is null) return;
        if (Period.To < Period.From) { Dialogs.ShowError("A data final é anterior à inicial."); return; }
        try
        {
            var p = Period;
            var userId = ActorId;
            switch (SelectedReport.Value)
            {
                case ReportKind.SalesByDay:
                {
                    var rows = await Runner.RunAsync<ReportService, IReadOnlyList<DailySalesRow>>(r => r.SalesByDayAsync(userId, p));
                    Show(rows, [Col("Data", nameof(DailySalesRow.Date), "dd/MM/yyyy (ddd)", 140), Col("Vendas", nameof(DailySalesRow.Sales), null, 75),
                        Col("Bruto", nameof(DailySalesRow.Gross), "C"), Col("Descontos", nameof(DailySalesRow.Discounts), "C"),
                        Col("Líquido", nameof(DailySalesRow.Net), "C"), Col("Devoluções", nameof(DailySalesRow.Returns), "C"),
                        Col("Faturamento", nameof(DailySalesRow.Revenue), "C"), Col("Ticket médio", nameof(DailySalesRow.AverageTicket), "C"),
                        Col("Canceladas", nameof(DailySalesRow.Cancelled), null, 95)],
                        $"{rows.Sum(r => r.Sales)} vendas · faturamento {rows.Sum(r => r.Revenue):C} · descontos {rows.Sum(r => r.Discounts):C} · " +
                        $"devoluções {rows.Sum(r => r.Returns):C} · {rows.Sum(r => r.Cancelled)} cancelada(s)");
                    break;
                }
                case ReportKind.Payments:
                {
                    var rows = await Runner.RunAsync<ReportService, IReadOnlyList<PaymentMethodRow>>(r => r.PaymentsByMethodAsync(userId, p));
                    Show(rows, [Col("Forma de pagamento", nameof(PaymentMethodRow.Label)), Col("Pagamentos", nameof(PaymentMethodRow.Count), null, 110),
                        Col("Valor", nameof(PaymentMethodRow.Amount), "C", 140)], $"Total recebido {rows.Sum(r => r.Amount):C}");
                    break;
                }
                case ReportKind.TopProducts:
                {
                    var rows = await Runner.RunAsync<ReportService, IReadOnlyList<TopProductRow>>(r => r.TopProductsAsync(userId, p));
                    var revenue = rows.Sum(r => r.Revenue);
                    var margin = rows.Sum(r => r.Margin);
                    Show(rows, [Col("Produto", nameof(TopProductRow.Description)), Col("Quantidade", nameof(TopProductRow.Quantity), "0.###", 100),
                        Col("Faturamento", nameof(TopProductRow.Revenue), "C"), Col("Custo", nameof(TopProductRow.Cost), "C"),
                        Col("Margem", nameof(TopProductRow.Margin), "C"), Col("Margem %", nameof(TopProductRow.MarginPercent), "0.0", 95)],
                        $"Faturamento {revenue:C} · margem estimada {margin:C} ({(revenue == 0 ? 0 : Money.Round(margin / revenue * 100)):0.0}%)");
                    break;
                }
                case ReportKind.CashSessions:
                {
                    var rows = (await Runner.RunAsync<ReportService, IReadOnlyList<CashSessionRow>>(r => r.CashSessionsAsync(userId, p)))
                        .Select(r => new CashRow(r)).ToList();
                    Show(rows, [Col("Caixa", nameof(CashRow.Terminal), null, 65), Col("Abertura", nameof(CashRow.Opened), null, 130),
                        Col("Operador", nameof(CashRow.OpenedBy)), Col("Fechamento", nameof(CashRow.Closed), null, 130),
                        Col("Esperado", nameof(CashRow.Expected), "C"), Col("Contado", nameof(CashRow.Counted), "C"),
                        Col("Diferença", nameof(CashRow.Difference), "C"), Col("Situação", nameof(CashRow.Status), null, 90)],
                        $"{rows.Count} caixa(s) · diferença total {rows.Sum(r => r.Difference ?? 0):C}. Selecione um caixa para reimprimir o fechamento.");
                    break;
                }
                case ReportKind.Divergences:
                {
                    var rows = await Runner.RunAsync<ReportService, IReadOnlyList<OperatorDivergenceRow>>(r => r.DivergencesByOperatorAsync(userId, p));
                    Show(rows, [Col("Operador", nameof(OperatorDivergenceRow.Operator)), Col("Caixas", nameof(OperatorDivergenceRow.Sessions), null, 80),
                        Col("Com diferença", nameof(OperatorDivergenceRow.SessionsWithDifference), null, 120),
                        Col("Faltas", nameof(OperatorDivergenceRow.Shortage), "C"), Col("Sobras", nameof(OperatorDivergenceRow.Surplus), "C"),
                        Col("Saldo", nameof(OperatorDivergenceRow.Net), "C")],
                        $"Faltas {rows.Sum(r => r.Shortage):C} · sobras {rows.Sum(r => r.Surplus):C}");
                    break;
                }
                case ReportKind.LowStock:
                {
                    var rows = await Runner.RunAsync<ReportService, IReadOnlyList<LowStockRow>>(r => r.LowStockAsync(userId));
                    Show(rows, [Col("Produto", nameof(LowStockRow.Name)), Col("Un", nameof(LowStockRow.Unit), null, 55),
                        Col("Estoque", nameof(LowStockRow.Stock), "0.###", 95), Col("Mínimo", nameof(LowStockRow.Minimum), "0.###", 95),
                        Col("Repor", nameof(LowStockRow.Missing), "0.###", 95)],
                        rows.Count == 0 ? "Nenhum produto abaixo do mínimo." : $"{rows.Count} produto(s) para repor (independe do período).");
                    break;
                }
            }
        }
        catch (DomainException ex)
        {
            Dialogs.ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private void ExportReport()
    {
        if (ReportRows.Count == 0) { Dialogs.ShowError("Gere o relatório antes de exportar."); return; }
        var name = $"{SelectedReport?.Label} {Period.From:yyyy-MM-dd} a {Period.To:yyyy-MM-dd}.csv".Replace('/', '-').Replace('(', ' ').Replace(')', ' ');
        var path = Dialogs.PickSaveFile("Exportar relatório", "Planilha CSV (*.csv)|*.csv", name);
        if (path is null) return;
        ReportExporter.WriteCsv(path, ReportColumns, ReportRows);
        Dialogs.ShowInfo($"Relatório exportado:\n{path}\n\nAbre direto no Excel ou LibreOffice.");
    }

    [RelayCommand]
    private async Task PrintReportAsync()
    {
        if (ReportRows.Count == 0) { Dialogs.ShowError("Gere o relatório antes de imprimir."); return; }
        var company = await Runner.RunAsync<CompanySettingsService, CompanySettingsModel>(s => s.GetAsync());
        var subtitle = SelectedReport?.Value == ReportKind.LowStock
            ? $"{company.TradeName ?? company.LegalName} · posição atual"
            : $"{company.TradeName ?? company.LegalName} · {Period.From:dd/MM/yyyy} a {Period.To:dd/MM/yyyy}";
        ReportExporter.Print(ReportExporter.BuildDocument(SelectedReport?.Label ?? "Relatório", subtitle, ReportColumns, ReportRows, ReportSummary),
            SelectedReport?.Label ?? "Relatório");
    }

    [RelayCommand]
    private async Task PrintCashClosingAsync()
    {
        if (SelectedReportRow is not CashRow row || _tickets is null) { Dialogs.ShowError("Selecione um caixa na lista."); return; }
        var report = await Runner.RunAsync<ReportService, CashClosingReport?>(r => r.CashClosingAsync(row.Row.SessionId));
        var company = await Runner.RunAsync<CompanySettingsService, CompanySettingsModel>(s => s.GetAsync());
        if (report is not null) _tickets.PrintCashClosing(report, company.TradeName ?? company.LegalName);
    }

    private void Show<T>(IReadOnlyList<T> rows, IReadOnlyList<GridColumn> columns, string summary) where T : notnull
    {
        ReportColumns = columns;
        ReportRows = rows.Cast<object>().ToList();
        ReportSummary = rows.Count == 0 ? "Nenhum dado no período." : summary;
    }

    private static GridColumn Col(string header, string path, string? format = null, double width = double.NaN) =>
        new(header, path, format, double.IsNaN(width) && format == "C" ? 120 : width);

    public sealed record CashRow(CashSessionRow Row)
    {
        public string Terminal => Row.Terminal;
        public string Opened => Row.OpenedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);
        public string OpenedBy => Row.OpenedBy;
        public string Closed => Row.ClosedAt?.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture) ?? "—";
        public decimal Expected => Row.Expected;
        public decimal? Counted => Row.Counted;
        public decimal? Difference => Row.Difference;
        public string Status => Row.Status == CashSessionStatus.Open ? "Aberto" : "Fechado";
    }
}
