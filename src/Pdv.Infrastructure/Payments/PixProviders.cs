using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pdv.Application.Payments;
using Pdv.Application.Security;

namespace Pdv.Infrastructure.Payments;

/// <summary>
/// PSP simulado para desenvolvimento, demonstração e treinamento: gera um BR Code válido e "recebe" o pagamento
/// sozinho depois de <see cref="PixSettings.SimulatorPaysAfterSeconds"/>. Nada sai do computador.
/// </summary>
public sealed class SimulatedPixProvider(TimeProvider clock) : IPixProvider
{
    private readonly ConcurrentDictionary<string, Charge> _charges = new();

    public string Name => PixSettings.Simulated;

    public Task<PspChargeCreated> CreateAsync(PspChargeRequest request, PixSettings settings, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        _charges[request.Txid] = new Charge(request.Amount, now, false);
        var code = BrCode.Dynamic($"pix.simulado.local/qr/v2/{request.Txid}", "LOJA SIMULADA", "SAO PAULO", request.Amount);
        return Task.FromResult(new PspChargeCreated(code, now.AddSeconds(request.ExpirationSeconds)));
    }

    public Task<PspChargeState> GetAsync(string txid, PixSettings settings, CancellationToken ct)
    {
        if (!_charges.TryGetValue(txid, out var charge)) throw new PixProviderException("Cobrança desconhecida no PSP simulado.");
        if (charge.Removed) return Task.FromResult(new PspChargeState(PspChargeStatus.Removed, null, null, null, null));
        var paidAt = charge.CreatedAt.AddSeconds(Math.Max(0, settings.SimulatorPaysAfterSeconds));
        return Task.FromResult(clock.GetUtcNow() >= paidAt
            ? new PspChargeState(PspChargeStatus.Paid, StableE2E(txid), charge.Amount, "Pagador Simulado", paidAt)
            : new PspChargeState(PspChargeStatus.Active, null, null, null, null));
    }

    public Task CancelAsync(string txid, PixSettings settings, CancellationToken ct)
    {
        if (_charges.TryGetValue(txid, out var charge)) _charges[txid] = charge with { Removed = true };
        return Task.CompletedTask;
    }

    public Task<PspRefundStatus> RefundAsync(string endToEndId, string refundId, decimal amount, PixSettings settings, CancellationToken ct) =>
        Task.FromResult(PspRefundStatus.Refunded);

    /// <summary>O mesmo txid sempre recebe o mesmo EndToEndId (consultas repetidas são consistentes).</summary>
    private static string StableE2E(string txid) =>
        "E" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(txid)))[..31];

    private sealed record Charge(decimal Amount, DateTimeOffset CreatedAt, bool Removed);
}

/// <summary>
/// API Pix padronizada pelo Banco Central (implementada por bancos e PSPs): OAuth2 client_credentials com certificado
/// do PSP (mTLS), cobrança imediata em PUT /cob/{txid}, consulta em GET /cob/{txid}, remoção via PATCH e devolução em
/// PUT /pix/{e2eid}/devolucao/{id}. <see cref="PixSettings.BaseUrl"/> é a raiz até antes de "/cob".
/// </summary>
public sealed class BacenPixProvider(Func<PixSettings, HttpMessageHandler>? handlerFactory = null) : IPixProvider, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _tokens = new();

    public string Name => PixSettings.BacenApi;

    public async Task<PspChargeCreated> CreateAsync(PspChargeRequest request, PixSettings settings, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["calendario"] = new JsonObject { ["expiracao"] = request.ExpirationSeconds },
            ["valor"] = new JsonObject { ["original"] = request.Amount.ToString("0.00", CultureInfo.InvariantCulture) },
            ["chave"] = settings.PixKey,
            ["solicitacaoPagador"] = request.Description,
        };
        var json = await SendAsync(HttpMethod.Put, $"cob/{request.Txid}", body, settings, ct);
        var copyPaste = json?["pixCopiaECola"]?.GetValue<string>()
            ?? throw new PixProviderException("O PSP não devolveu o código \"copia e cola\" (pixCopiaECola).");
        var created = ReadDate(json["calendario"]?["criacao"]) ?? DateTimeOffset.UtcNow;
        var expiration = json["calendario"]?["expiracao"]?.GetValue<int>() ?? request.ExpirationSeconds;
        return new PspChargeCreated(copyPaste, created.AddSeconds(expiration));
    }

    public async Task<PspChargeState> GetAsync(string txid, PixSettings settings, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"cob/{txid}", null, settings, ct)
            ?? throw new PixProviderException("Resposta vazia do PSP.");
        var status = json["status"]?.GetValue<string>();
        if (status == "CONCLUIDA" && json["pix"] is JsonArray { Count: > 0 } pix)
        {
            var paid = pix[0]!;
            return new PspChargeState(PspChargeStatus.Paid, paid["endToEndId"]?.GetValue<string>(),
                decimal.Parse(paid["valor"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture),
                paid["pagador"]?["nome"]?.GetValue<string>(), ReadDate(paid["horario"]));
        }
        return status is "REMOVIDA_PELO_USUARIO_RECEBEDOR" or "REMOVIDA_PELO_PSP"
            ? new PspChargeState(PspChargeStatus.Removed, null, null, null, null)
            : new PspChargeState(PspChargeStatus.Active, null, null, null, null);
    }

    public Task CancelAsync(string txid, PixSettings settings, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"cob/{txid}", new JsonObject { ["status"] = "REMOVIDA_PELO_USUARIO_RECEBEDOR" }, settings, ct);

    public async Task<PspRefundStatus> RefundAsync(string endToEndId, string refundId, decimal amount, PixSettings settings, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Put, $"pix/{endToEndId}/devolucao/{refundId}",
            new JsonObject { ["valor"] = amount.ToString("0.00", CultureInfo.InvariantCulture) }, settings, ct);
        return json?["status"]?.GetValue<string>() switch
        {
            "DEVOLVIDO" => PspRefundStatus.Refunded,
            "NAO_REALIZADO" => PspRefundStatus.Failed,
            _ => PspRefundStatus.Processing,
        };
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, PixSettings settings, CancellationToken ct)
    {
        var client = Client(settings);
        var token = await TokenAsync(client, settings, ct);
        using var request = new HttpRequestMessage(method, $"{settings.BaseUrl!.TrimEnd('/')}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await SendWithTimeoutAsync(client, request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) _tokens.TryRemove(Key(settings), out _);
        if (!response.IsSuccessStatusCode) throw new PixProviderException(ErrorMessage(response.StatusCode, text));
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private async Task<string> TokenAsync(HttpClient client, PixSettings settings, CancellationToken ct)
    {
        var key = Key(settings);
        if (_tokens.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Token;

        var tokenUrl = settings.TokenUrl ?? new Uri(new Uri(settings.BaseUrl!), "/oauth/token").ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}")));
        using var response = await SendWithTimeoutAsync(client, request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new PixProviderException($"Autenticação no PSP recusada ({ErrorMessage(response.StatusCode, text)}). Confira client ID, secret e certificado.");
        var json = JsonNode.Parse(text);
        var token = json?["access_token"]?.GetValue<string>() ?? throw new PixProviderException("O PSP não devolveu o token de acesso.");
        var expires = json["expires_in"]?.GetValue<int>() ?? 300;
        _tokens[key] = (token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expires - 60)));
        return token;
    }

    private static async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            return await client.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PixProviderException("o PSP não respondeu a tempo.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PixProviderException($"sem conexão com o PSP ({ex.Message}).", ex);
        }
    }

    private HttpClient Client(PixSettings settings) => _clients.GetOrAdd(Key(settings), _ =>
        new HttpClient(handlerFactory?.Invoke(settings) ?? CreateHandler(settings)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan });

    /// <summary>Certificado do PSP (mTLS), exigido pela maioria das APIs Pix.</summary>
    private static SocketsHttpHandler CreateHandler(PixSettings settings)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(settings.CertificatePath) && File.Exists(settings.CertificatePath))
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword);
            handler.SslOptions.ClientCertificates = [cert];
        }
        return handler;
    }

    private static string Key(PixSettings s) => $"{s.BaseUrl}|{s.TokenUrl}|{s.ClientId}|{s.CertificatePath}|{s.ClientSecret?.GetHashCode(StringComparison.Ordinal)}";

    private static DateTimeOffset? ReadDate(JsonNode? node) =>
        node?.GetValue<string>() is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Erros da API Pix seguem RFC 7807 (title/detail).</summary>
    private static string ErrorMessage(HttpStatusCode status, string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            var detail = json?["detail"]?.GetValue<string>() ?? json?["title"]?.GetValue<string>() ?? json?["error_description"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(detail)) return $"{(int)status} - {detail}";
        }
        catch (JsonException) { }
        return $"HTTP {(int)status}";
    }
}

/// <summary>Configuração do Pix em {pasta do servidor}\pix.json, com client secret e senha do certificado cifrados (DPAPI).</summary>
public sealed class FilePixSettingsStore(string folder) : IPixSettingsStore
{
    private const string Purpose = "SistemaPDV.Pix.v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string FilePath => Path.Combine(folder, "pix.json");

    public PixSettings Load()
    {
        if (!File.Exists(FilePath)) return PixSettings.Disabled;
        var s = JsonSerializer.Deserialize<PixSettings>(File.ReadAllText(FilePath)) ?? PixSettings.Disabled;
        return s with { ClientSecret = Dpapi.Unprotect(s.ClientSecret, Purpose), CertificatePassword = Dpapi.Unprotect(s.CertificatePassword, Purpose) };
    }

    public void Save(PixSettings settings)
    {
        Directory.CreateDirectory(folder);
        var stored = settings with
        {
            ClientSecret = Dpapi.Protect(settings.ClientSecret, Purpose),
            CertificatePassword = Dpapi.Protect(settings.CertificatePassword, Purpose),
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, Json));
    }

    public string ImportCertificate(byte[] pfx)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "certificado-psp.pfx");
        File.WriteAllBytes(path, pfx);
        return path;
    }
}

/// <summary>Usado quando o servidor não define outro (testes): Pix integrado desligado até ser configurado.</summary>
public sealed class InMemoryPixSettingsStore : IPixSettingsStore
{
    public PixSettings Current { get; set; } = PixSettings.Disabled;
    public PixSettings Load() => Current;
    public void Save(PixSettings settings) => Current = settings;
    public string ImportCertificate(byte[] pfx) => "certificado-psp.pfx";
}
