using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Pdv.Desktop.Controls;

/// <summary>
/// ItemsControl cujas linhas expõem o conteúdo real (textos e botões) à automação e aos leitores de tela.
/// O ItemsControl padrão do WPF mostra cada linha como um "item de dados" vazio, escondendo os botões dela.
/// </summary>
public sealed class AccessibleItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
