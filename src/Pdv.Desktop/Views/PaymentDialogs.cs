using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pdv.Application.Payments;
using Pdv.Client;
using Pdv.Domain.Common;
using Pdv.Domain.Payments;
using QRCoder;
using Serilog;

namespace Pdv.Desktop.Views;

/// <summary>
/// Pix com QR Code dinâmico: pede a cobrança ao servidor, mostra o QR e consulta a cada 2 s até o PSP confirmar.
/// Fecha sozinho quando o pagamento é confirmado.
/// </summary>
public sealed class PixPaymentDialog : Window
{
    private readonly ServerRunner _runner;
    private readonly Guid _terminalId;
    private readonly Guid _requestId;
    private readonly decimal _amount;
    private readonly Image _qr = new() { Width = 240, Height = 240, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock _status = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox _copyPaste = new() { IsReadOnly = true, FontSize = 11, TextWrapping = TextWrapping.Wrap, Height = 54, Margin = new Thickness(0, 4, 0, 14) };
    private readonly Button _cancel;
    private readonly Button _retry;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _checking;
    private bool _done;

    public PixPaymentDialog(ServerRunner runner, Guid terminalId, Guid requestId, decimal amount)
    {
        _runner = runner;
        _terminalId = terminalId;
        _requestId = requestId;
        _amount = amount;
        DialogChrome.Apply(this, "Pagamento com Pix", 400);
        var panel = DialogChrome.Panel(this, $"Pix de {amount:C}", "Icon.Pix");
        panel.Children.Add(new TextBlock { Text = "Peça ao cliente para ler o QR Code no aplicativo do banco.", TextWrapping = TextWrapping.Wrap,
            Foreground = DialogChrome.Brush(this, "TextSecondaryBrush") });
        panel.Children.Add(new Border { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(6),
            Margin = new Thickness(0, 10, 0, 0), Child = _qr });
        panel.Children.Add(_status);
        panel.Children.Add(new TextBlock { Text = "Pix copia e cola", Style = (Style)FindResource("Caption") });
        panel.Children.Add(_copyPaste);

        _retry = new Button { Content = "Gerar novo QR", MinWidth = 120, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        _retry.Click += async (_, _) => await CreateAsync();
        _cancel = new Button { Content = "Cancelar Pix", MinWidth = 120, IsCancel = true };
        _cancel.Click += async (_, _) => await CancelAsync();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _retry, _cancel } });
        Content = panel;

        _timer.Tick += async (_, _) => await CheckAsync();
        Loaded += async (_, _) => await CreateAsync();
        Closing += (_, e) => { if (!_done) { e.Cancel = true; _ = CancelAsync(); } };
    }

    /// <summary>Cobrança confirmada (quando o diálogo fecha com sucesso).</summary>
    public PixChargeView? Charge { get; private set; }

    private async Task CreateAsync()
    {
        _retry.Visibility = Visibility.Collapsed;
        _status.Text = "Gerando o QR Code...";
        try
        {
            Charge = await _runner.RunAsync<PixService, PixChargeView>(s => s.CreateChargeAsync(_terminalId, _requestId, _amount, default));
            _qr.Source = QrImage.Create(Charge.CopyPaste);
            _copyPaste.Text = Charge.CopyPaste;
            Show(Charge);
            _timer.Start();
        }
        catch (Exception ex) when (ex is DomainException or ServerException)
        {
            _status.Text = ex.Message;
            _status.Foreground = DialogChrome.Brush(this, "DangerBrush");
            _retry.Visibility = Visibility.Visible;
        }
    }

    private async Task CheckAsync()
    {
        if (_checking || Charge is null) return;
        _checking = true;
        try
        {
            var id = Charge.Id;
            Charge = await _runner.RunAsync<PixService, PixChargeView>(s => s.CheckAsync(_terminalId, id, default));
            Show(Charge);
        }
        catch (Exception ex) when (ex is DomainException or ServerException)
        {
            Log.Warning("Consulta do Pix falhou: {Message}", ex.Message);
            _status.Text = $"Aguardando pagamento... (sem resposta: {ex.Message})";
        }
        finally
        {
            _checking = false;
        }
    }

    private void Show(PixChargeView charge)
    {
        switch (charge.Status)
        {
            case PixChargeStatus.Paid:
                _timer.Stop();
                _status.Text = $"Pagamento confirmado{(charge.PayerName is { } payer ? $" — {payer}" : "")}.";
                _status.Foreground = DialogChrome.Brush(this, "SuccessBrush");
                Finish(true);
                break;
            case PixChargeStatus.Expired:
                _timer.Stop();
                _status.Text = "O QR Code expirou sem pagamento.";
                _retry.Visibility = Visibility.Visible;
                break;
            case PixChargeStatus.Cancelled or PixChargeStatus.Refunded:
                _timer.Stop();
                _status.Text = "Cobrança cancelada.";
                break;
            default:
                var left = charge.ExpiresAt - DateTimeOffset.UtcNow;
                _status.Text = $"Aguardando pagamento... expira em {Math.Max(0, (int)left.TotalMinutes)}:{Math.Max(0, left.Seconds):00}";
                break;
        }
    }

    private async Task CancelAsync()
    {
        _timer.Stop();
        if (Charge is null || Charge.Status != PixChargeStatus.Active) { Finish(Charge?.Status == PixChargeStatus.Paid); return; }
        _cancel.IsEnabled = false;
        try
        {
            var id = Charge.Id;
            Charge = await _runner.RunAsync<PixService, PixChargeView>(s => s.CancelAsync(_terminalId, id, default));
        }
        catch (Exception ex) when (ex is DomainException or ServerException)
        {
            Log.Warning("Cancelamento do Pix falhou: {Message}", ex.Message);
        }
        // Pago no último instante: vale como pagamento.
        Finish(Charge.Status == PixChargeStatus.Paid);
    }

    private void Finish(bool paid)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        if (paid)
        {
            var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            close.Tick += (_, _) => { close.Stop(); DialogResult = true; };
            close.Start();
        }
        else
        {
            DialogResult = false;
        }
    }
}

/// <summary>Pagamento com cartão pelo TEF: mostra as mensagens do pinpad até aprovar ou negar.</summary>
public sealed class TefPaymentDialog : Window, IDisposable
{
    private readonly TefCoordinator _tef;
    private readonly TefRequest _request;
    private readonly TextBlock _status = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18), MinHeight = 48 };
    private readonly Button _cancel = new() { Content = "Cancelar", MinWidth = 110, IsCancel = true };
    private readonly CancellationTokenSource _cts = new();
    private bool _running;

    public TefPaymentDialog(TefCoordinator tef, TefRequest request)
    {
        _tef = tef;
        _request = request;
        DialogChrome.Apply(this, "Pagamento com cartão", 440);
        var panel = DialogChrome.Panel(this, $"{(request.Type == TefCardType.Credit ? "Crédito" : "Débito")} — {request.Amount:C}", "Icon.Card");
        panel.Children.Add(_status);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel } });
        Content = panel;
        _cancel.Click += (_, _) => { if (_running) _cts.Cancel(); else DialogResult = Outcome?.Approved == true; };
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => Dispose();
    }

    public void Dispose() => _cts.Dispose();

    public TefOutcome? Outcome { get; private set; }

    private async Task RunAsync()
    {
        _running = true;
        try
        {
            Outcome = await _tef.PayAsync(_request, new Progress<string>(m => _status.Text = m), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Outcome = TefOutcome.Declined("Operação cancelada no caixa.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha no TEF");
            Outcome = TefOutcome.Declined($"Falha no TEF: {ex.Message}");
        }
        _running = false;
        if (Outcome.Approved)
        {
            _status.Text = $"{Outcome.Message} {Outcome.Approval!.CardBrandName} · NSU {Outcome.Approval.Nsu}";
            _status.Foreground = DialogChrome.Brush(this, "SuccessBrush");
            await Task.Delay(900);
            DialogResult = true;
        }
        else
        {
            _status.Text = Outcome.Message;
            _status.Foreground = DialogChrome.Brush(this, "DangerBrush");
            _cancel.Content = "Fechar";
        }
    }
}

internal static class QrImage
{
    public static BitmapImage Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var image = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }
}
