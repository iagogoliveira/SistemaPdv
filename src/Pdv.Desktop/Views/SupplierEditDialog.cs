using System.Windows;
using System.Windows.Controls;
using Pdv.Application.Purchases;

namespace Pdv.Desktop.Views;

/// <summary>Cadastro de fornecedor: nome, CNPJ/CPF, telefone e e-mail.</summary>
public sealed class SupplierEditDialog : Window
{
    private readonly TextBox _name = new() { MaxLength = 150 };
    private readonly TextBox _document = new() { MaxLength = 18, Tag = "CNPJ ou CPF (opcional)" };
    private readonly TextBox _phone = new() { MaxLength = 20, Tag = "opcional" };
    private readonly TextBox _email = new() { MaxLength = 120, Tag = "opcional" };
    private readonly Guid? _id;

    public SupplierEditDialog(SupplierModel? existing)
    {
        _id = existing?.Id;
        var title = existing is null ? "Novo fornecedor" : "Editar fornecedor";
        DialogChrome.Apply(this, title, 460);
        var panel = DialogChrome.Panel(this, title, "Icon.Truck");

        void Field(string label, TextBox box, string? value)
        {
            panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption") });
            box.Text = value ?? "";
            box.Margin = new Thickness(0, 4, 0, 12);
            panel.Children.Add(box);
        }
        Field("Razão social / nome", _name, existing?.Name);
        Field("CNPJ / CPF", _document, existing?.Document is { } d ? Domain.Fiscal.TaxDocuments.Format(d) : null);
        Field("Telefone", _phone, existing?.Phone);
        Field("E-mail", _email, existing?.Email);
        panel.Children.Add(DialogButtons.Create(this, "Salvar"));
        Content = panel;
        Loaded += (_, _) => _name.Focus();
    }

    public SupplierModel Model => new(_id, _name.Text, _document.Text, _phone.Text, _email.Text);
}
