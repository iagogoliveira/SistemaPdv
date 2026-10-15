using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pdv.Client;

public enum TefCardType
{
    Credit,
    Debit,
}

public sealed record TefRequest(TefCardType Type, decimal Amount, int Installments, string SaleReference);

/// <summary>Transação aprovada pelo TEF, pendente de confirmação até a venda ser gravada no servidor.</summary>
public sealed record TefApproval(
    TefCardType Type,
    decimal Amount,
    string Nsu,
    string? AuthorizationCode,
    string? CardBrand,
    string? CardBrandName,
    string? AcquirerCnpj,
    string? AcquirerName,
    string? MaskedCard,
    DateTimeOffset ApprovedAt,
    string? CustomerReceipt,
    string? MerchantReceipt);

public sealed record TefOutcome(bool Approved, string Message, TefApproval? Approval)
{
    public static TefOutcome Declined(string message) => new(false, message, null);
}

/// <summary>
/// Porta para o TEF instalado no caixa (pinpad + cliente da credenciadora/SiTef/PayGo). Fluxo em três fases, como exigem
/// os TEFs homologados: <see cref="PayAsync"/> aprova; <see cref="ConfirmAsync"/> efetiva depois que a venda é gravada;
/// <see cref="UndoAsync"/> desfaz se a venda não for concluída. <see cref="ReverseAsync"/> estorna uma venda cancelada.
/// </summary>
public interface ITefClient
{
    string Name { get; }

    Task<TefOutcome> PayAsync(TefRequest request, IProgress<string> progress, CancellationToken ct);

    Task ConfirmAsync(TefApproval approval, CancellationToken ct);

    Task UndoAsync(TefApproval approval, CancellationToken ct);

    Task<TefOutcome> ReverseAsync(TefApproval original, IProgress<string> progress, CancellationToken ct);
}

/// <summary>
/// TEF simulado (desenvolvimento e treinamento): aprova após alguns segundos; valores terminados em ,13 são negados.
/// Não fala com pinpad nem com credenciadora.
/// </summary>
public sealed class SimulatedTefClient(TimeSpan? delay = null) : ITefClient
{
    public const string ProviderName = "Simulado";
    public const string AcquirerCnpj = "11222333000181";

    private readonly TimeSpan _delay = delay ?? TimeSpan.FromSeconds(2);

    public string Name => ProviderName;

    public async Task<TefOutcome> PayAsync(TefRequest request, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Insira, passe ou aproxime o cartão no pinpad (simulado)...");
        await Task.Delay(_delay / 2, ct);
        progress.Report("Processando com a credenciadora...");
        await Task.Delay(_delay / 2, ct);
        if (decimal.Remainder(decimal.Round(request.Amount * 100), 100) == 13)
            return TefOutcome.Declined("Transação negada pelo emissor (simulador: valores terminados em ,13 são negados).");

        var nsu = RandomNumberGenerator.GetInt32(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
        var auth = RandomNumberGenerator.GetInt32(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
        var (brand, brandName) = request.Type == TefCardType.Credit ? ("02", "MASTERCARD") : ("06", "ELO");
        var now = DateTimeOffset.Now;
        var receipt = Receipt(request, nsu, auth, brandName, now);
        return new TefOutcome(true, "Transação aprovada.",
            new TefApproval(request.Type, request.Amount, nsu, auth, brand, brandName, AcquirerCnpj, "CREDENCIADORA SIMULADA",
                "************1234", now, receipt, receipt));
    }

    public Task ConfirmAsync(TefApproval approval, CancellationToken ct) => Task.CompletedTask;

    public Task UndoAsync(TefApproval approval, CancellationToken ct) => Task.CompletedTask;

    public async Task<TefOutcome> ReverseAsync(TefApproval original, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Estornando no pinpad (simulado)...");
        await Task.Delay(_delay / 2, ct);
        return new TefOutcome(true, $"Estorno da transação NSU {original.Nsu} aprovado.", original with
        {
            Nsu = RandomNumberGenerator.GetInt32(100_000, 999_999).ToString(CultureInfo.InvariantCulture),
            ApprovedAt = DateTimeOffset.Now,
        });
    }

    private static string Receipt(TefRequest r, string nsu, string auth, string brand, DateTimeOffset at)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CREDENCIADORA SIMULADA");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{brand} - {(r.Type == TefCardType.Credit ? "CREDITO" : "DEBITO")}");
        sb.AppendLine("************1234");
        sb.AppendLine(CultureInfo.InvariantCulture, $"VALOR: R$ {r.Amount.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"))}");
        if (r.Type == TefCardType.Credit && r.Installments > 1) sb.AppendLine(CultureInfo.InvariantCulture, $"PARCELADO EM {r.Installments}X");
        sb.AppendLine(CultureInfo.InvariantCulture, $"NSU: {nsu}   AUT: {auth}");
        sb.AppendLine(at.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine("TRANSACAO SIMULADA - SEM VALOR");
        return sb.ToString();
    }
}

/// <summary>
/// Transações aprovadas e ainda não confirmadas, em {pasta}\tef-pendentes.json. Se o caixa desligar, na volta elas são
/// confirmadas (venda gravada) ou desfeitas (venda não concluída) — o cliente nunca é cobrado sem venda.
/// </summary>
public sealed class TefPendingStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public string FilePath { get; } = Path.Combine(folder, "tef-pendentes.json");

    public IReadOnlyList<TefApproval> Load()
    {
        lock (_gate) return Read();
    }

    public void Add(TefApproval approval)
    {
        lock (_gate) Write([.. Read().Where(a => a.Nsu != approval.Nsu), approval]);
    }

    public void Remove(string nsu)
    {
        lock (_gate) Write(Read().Where(a => a.Nsu != nsu).ToList());
    }

    private List<TefApproval> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<TefApproval>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private void Write(List<TefApproval> list)
    {
        if (list.Count == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
    }
}

/// <summary>Une o TEF do caixa ao registro de pendências: toda aprovação fica guardada até confirmar ou desfazer.</summary>
public sealed class TefCoordinator(ITefClient client, TefPendingStore pending)
{
    public string Name => client.Name;

    public IReadOnlyList<TefApproval> Pending => pending.Load();

    public async Task<TefOutcome> PayAsync(TefRequest request, IProgress<string> progress, CancellationToken ct)
    {
        var outcome = await client.PayAsync(request, progress, ct);
        if (outcome.Approved && outcome.Approval is { } approval) pending.Add(approval);
        return outcome;
    }

    public async Task ConfirmAsync(IEnumerable<TefApproval> approvals, CancellationToken ct)
    {
        foreach (var a in approvals)
        {
            await client.ConfirmAsync(a, ct);
            pending.Remove(a.Nsu);
        }
    }

    public async Task UndoAsync(IEnumerable<TefApproval> approvals, CancellationToken ct)
    {
        foreach (var a in approvals)
        {
            await client.UndoAsync(a, ct);
            pending.Remove(a.Nsu);
        }
    }

    public Task<TefOutcome> ReverseAsync(TefApproval original, IProgress<string> progress, CancellationToken ct) =>
        client.ReverseAsync(original, progress, ct);
}
