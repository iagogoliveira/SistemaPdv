using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Pdv.Desktop.ViewModels;

namespace Pdv.Desktop.Views;

public partial class AdminWindow : Window
{
    private readonly AdminViewModel _viewModel;

    public AdminWindow(AdminViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
        // Abre na primeira aba que o usuário pode ver.
        Loaded += (_, _) => Tabs.SelectedItem = Tabs.Items.OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(t => t.Visibility == Visibility.Visible);
    }

    /// <summary>Cada relatório tem colunas próprias: a grade é remontada quando o relatório muda.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AdminViewModel.ReportColumns)) return;
        ReportGrid.Columns.Clear();
        foreach (var c in _viewModel.ReportColumns)
        {
            var numeric = c.Format is not null && !c.Format.StartsWith("dd", StringComparison.Ordinal);
            ReportGrid.Columns.Add(new DataGridTextColumn
            {
                Header = c.Header.ToUpperInvariant(),
                Binding = new Binding(c.Path) { StringFormat = c.Format is null ? null : "{0:" + c.Format + "}" },
                Width = double.IsNaN(c.Width) ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(c.Width),
                ElementStyle = numeric ? (Style)FindResource("NumericCell") : null,
            });
        }
    }

    private void OnCertPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.CertificatePasswordInput = CertPasswordBox.Password;

    private void OnCscTokenChanged(object sender, RoutedEventArgs e) =>
        _viewModel.CscTokenInput = CscTokenBox.Password;

    private void OnTaxProfileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanManageCompany) _viewModel.EditSelectedTaxProfileCommand.Execute(null);
    }

    private void OnProductDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanManageProducts) _viewModel.EditSelectedProductCommand.Execute(null);
    }
}
