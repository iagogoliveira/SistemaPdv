using System.Collections.Concurrent;
using System.Net;
using Pdv.Application.Terminals;

namespace Pdv.Server;

/// <summary>
/// Valida a chave do caixa em cada chamada (com cache curto: uma chave revogada para de valer em até 30 s)
/// e registra quando cada caixa foi visto pela última vez.
/// </summary>
public sealed class TerminalGate(IServiceScopeFactory scopes, TimeProvider clock, ILogger<TerminalGate> log)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (TerminalInfo Terminal, DateTimeOffset Until)> _cache = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _touched = new();

    public async Task<TerminalInfo?> AuthenticateAsync(string? accessKey, string? appVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessKey)) return null;
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(accessKey, out var hit) && hit.Until > now) return hit.Terminal;

        await using var scope = scopes.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TerminalRegistry>();
        var terminal = await registry.FindByAccessKeyAsync(accessKey, ct);
        if (terminal is null)
        {
            _cache.TryRemove(accessKey, out _);
            return null;
        }
        _cache[accessKey] = (terminal, now + CacheFor);

        if (!_touched.TryGetValue(terminal.Id, out var last) || now - last > TouchEvery)
        {
            _touched[terminal.Id] = now;
            try { await registry.TouchAsync(terminal.Id, appVersion, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Falha ao registrar atividade do caixa {Code}", terminal.Code); }
        }
        return terminal;
    }

    public void Forget() => _cache.Clear();
}

/// <summary>Limita tentativas erradas do código de conexão: 5 erros por endereço bloqueiam por 5 minutos.</summary>
public sealed class PairingThrottle(TimeProvider clock)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> _failures = new();

    public bool IsBlocked(IPAddress? address) =>
        _failures.TryGetValue(Key(address), out var f) && f.Failures >= MaxFailures && clock.GetUtcNow() - f.Since < Window;

    public void Fail(IPAddress? address) =>
        _failures.AddOrUpdate(Key(address), _ => (1, clock.GetUtcNow()),
            (_, f) => clock.GetUtcNow() - f.Since >= Window ? (1, clock.GetUtcNow()) : (f.Failures + 1, f.Since));

    public void Succeed(IPAddress? address) => _failures.TryRemove(Key(address), out _);

    private static string Key(IPAddress? address) => address?.ToString() ?? "?";
}

/// <summary>
/// Modo manutenção (restauração de backup): novas chamadas recebem 503 e a restauração espera as chamadas
/// em andamento terminarem, para nenhuma venda ficar pela metade.
/// </summary>
public sealed class MaintenanceGate
{
    private int _inFlight;
    private volatile string? _reason;

    public string? Reason => _reason;

    public bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (_reason is null) return true;
        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    public void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>Entra em manutenção a partir de uma chamada em andamento (a própria restauração conta como 1).</summary>
    public async Task<bool> BeginAsync(string reason, TimeSpan wait, CancellationToken ct)
    {
        _reason = reason;
        var until = DateTime.UtcNow + wait;
        while (Volatile.Read(ref _inFlight) > 1)
        {
            if (DateTime.UtcNow > until)
            {
                _reason = null;
                return false;
            }
            await Task.Delay(100, ct);
        }
        return true;
    }

    public void End() => _reason = null;
}
