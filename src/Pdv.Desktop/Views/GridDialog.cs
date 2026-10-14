using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Pdv.Desktop.Views;

public sealed record GridColumn(string Header, string Path, string? Format = null, double Width = double.NaN);

/// <summary>Lista em grade para consulta ou seleção (vendas recentes, histórico de estoque).</summary>
public sealed class GridDialog : Window
{
    private readonly DataGrid _grid;

    public GridDialog(string title, IEnumerable items, IReadOnlyList<GridColumn> columns, bool selectable, Func<object, bool>? canSelect = null)
    {
        Title = title;
        Width = 760;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 14;

        _grid = new DataGrid
        {
            ItemsSource = items,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeight = 30,
            Margin = new Thickness(12, 12, 12, 0),
        };
        foreach (var c in columns)
        {
            var binding = new Binding(c.Path) { StringFormat = c.Format };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = c.Header,
                Binding = binding,
                Width = double.IsNaN(c.Width) ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(c.Width),
            });
        }

        var panel = new DockPanel();
        StackPanel buttons;
        if (selectable)
        {
            buttons = DialogButtons.Create(this, "Selecionar");
            var ok = (Button)buttons.Children[1];
            ok.IsEnabled = false;
            _grid.SelectionChanged += (_, _) => ok.IsEnabled = _grid.SelectedItem is { } item && (canSelect?.Invoke(item) ?? true);
            _grid.MouseDoubleClick += (_, _) => { if (ok.IsEnabled) DialogResult = true; };
            _grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && ok.IsEnabled) { DialogResult = true; e.Handled = true; } };
        }
        else
        {
            buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(new Button { Content = "Fechar", IsCancel = true, IsDefault = true, MinWidth = 80 });
        }
        buttons.Margin = new Thickness(8);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(_grid);
        Content = panel;

        Loaded += (_, _) =>
        {
            if (_grid.Items.Count > 0) _grid.SelectedIndex = 0;
            _grid.Focus();
        };
    }

    public object? SelectedItem => _grid.SelectedItem;
}
