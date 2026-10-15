using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pdv.Application.Remote;
using Pdv.Application.Terminals;
using Pdv.Domain.Common;

namespace Pdv.Client;

/// <summary>Falha ao falar com o servidor da loja (mensagem pronta para o operador).</summary>
public class ServerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Servidor desligado, fora da rede, sem resposta ou em manutenção. A operação pode ser repetida.</summary>
public sealed class ServerUnavailableException(string message, Exception? inner = null) : ServerException(message, inner);

/// <summary>A chave deste caixa não é aceita (nunca conectado, desconectado na retaguarda ou banco restaurado).</summary>
public sealed class TerminalNotPairedException(string message) : ServerException(message);

/// <summary>Caixa e servidor em versões diferentes.</summary>
public sealed class VersionMismatchException(string message) : ServerException(message);

/// <summary>Chamadas HTTP ao servidor da loja. Cada caixa se identifica pela chave recebida ao ser conectado.</summary>
public sealed class PdvServerClient(HttpClient http)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    public string? AccessKey { get; set; }

    public Uri? BaseAddress => http.BaseAddress;

    public static HttpClient CreateHttpClient(Uri baseAddress) => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        BaseAddress = baseAddress,
        Timeout = Timeout.InfiniteTimeSpan, // cada chamada define o próprio prazo
    };

    /// <summary>"SERVIDOR", "192.168.0.10" ou "servidor:5080" → http://servidor:5080/.</summary>
    public static Uri ParseAddress(string address)
    {
        address = address.Trim().TrimEnd('/');
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        var uri = new UriBuilder(address);
        if (uri.Uri.IsDefaultPort && !address.EndsWith(":80", StringComparison.Ordinal)) uri.Port = RemoteApi.DefaultPort;
        uri.Path = "/";
        return uri.Uri;
    }

    public Task<ServerHealth> GetHealthAsync(CancellationToken ct = default) =>
        SendAsync<ServerHealth>(HttpMethod.Get, "api/health", null, TimeSpan.FromSeconds(10), ct);

    public Task<PairResponse> PairAsync(string machineName, string? pairingCode, CancellationToken ct = default) =>
        SendAsync<PairResponse>(HttpMethod.Post, "api/terminals/pair",
            JsonContent.Create(new PairRequest(machineName, pairingCode), options: RemoteApi.Json), TimeSpan.FromSeconds(20), ct);

    public Task<TerminalInfo> GetCurrentTerminalAsync(CancellationToken ct = default) =>
        SendAsync<TerminalInfo>(HttpMethod.Get, "api/terminals/me", null, TimeSpan.FromSeconds(10), ct);

    /// <summary>Chama serviço/método com os argumentos já em JSON; devolve o resultado desserializado como <paramref name="resultType"/>.</summary>
    public async Task<object?> InvokeAsync(string service, string method, JsonArray args, Type? resultType, TimeSpan timeout,
        CancellationToken ct = default)
    {
        var content = new StringContent(args.ToJsonString(RemoteApi.Json), System.Text.Encoding.UTF8, "application/json");
        using var response = await SendRawAsync(HttpMethod.Post, $"api/rpc/{service}/{method}", content, timeout, ct);
        if (resultType is null) return null;
        var json = await response.Content.ReadAsStringAsync(ct);
        return json.Length == 0 ? null : JsonSerializer.Deserialize(json, resultType, RemoteApi.Json);
    }

    private async Task<T> SendAsync<T>(HttpMethod verb, string path, HttpContent? content, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendRawAsync(verb, path, content, timeout, ct);
        return (await response.Content.ReadFromJsonAsync<T>(RemoteApi.Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod verb, string path, HttpContent? content, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(verb, path) { Content = content };
        request.Headers.Add(RemoteApi.VersionHeader, RemoteApi.Version);
        if (AccessKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ServerUnavailableException(
                $"O servidor da loja não respondeu a tempo ({ServerName}). A operação pode ou não ter sido concluída.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ServerUnavailableException(
                $"Sem conexão com o servidor da loja ({ServerName}).\nVerifique se o computador do servidor está ligado e na rede.", ex);
        }

        if (response.IsSuccessStatusCode) return response;
        try
        {
            throw await ToExceptionAsync(response, ct);
        }
        finally
        {
            response.Dispose();
        }
    }

    private string ServerName => http.BaseAddress?.Authority ?? "?";

    private static async Task<Exception> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        RemoteError? error = null;
        try { error = await response.Content.ReadFromJsonAsync<RemoteError>(RemoteApi.Json, ct); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }

        var message = error?.Message ?? $"O servidor respondeu {(int)response.StatusCode} ({response.ReasonPhrase}).";
        return (error?.Kind, response.StatusCode) switch
        {
            (RemoteError.Domain, _) => new DomainException(message),
            (RemoteError.Unauthorized, _) or (_, HttpStatusCode.Unauthorized) => new TerminalNotPairedException(message),
            (RemoteError.VersionMismatch, _) => new VersionMismatchException(message),
            (RemoteError.Maintenance, _) or (_, HttpStatusCode.ServiceUnavailable) => new ServerUnavailableException(message),
            _ => new ServerException(message),
        };
    }
}
