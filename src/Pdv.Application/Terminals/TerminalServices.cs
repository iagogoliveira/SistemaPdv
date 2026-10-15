using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Cash;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;
using Pdv.Domain.Stores;

namespace Pdv.Application.Terminals;

/// <summary>O que o caixa precisa saber de si mesmo (sem expor a entidade).</summary>
public sealed record TerminalInfo(Guid Id, Guid StoreId, string Code, string MachineName)
{
    public static TerminalInfo From(Terminal t) => new(t.Id, t.StoreId, t.Code, t.MachineName);
}

public sealed record TerminalListItem(Guid Id, string Code, string MachineName, bool IsActive, bool IsPaired,
    DateTimeOffset? PairedAt, DateTimeOffset? LastSeenAt, string? AppVersion);

public sealed record TerminalKey(Guid Id, Guid StoreId, string Code, string MachineName, string AccessKeyHash, DateTimeOffset? PairedAt);

/// <summary>Chave de acesso do caixa ao servidor: 256 bits aleatórios; o banco guarda só o SHA-256.</summary>
public static class TerminalAccessKey
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>Uso exclusivo do servidor: conecta caixas e valida a chave em cada chamada.</summary>
public sealed class TerminalRegistry(IPdvDbContext db, TimeProvider clock)
{
    /// <summary>Registra o computador como caixa (ou reaproveita o registro existente) e troca a chave de acesso.</summary>
    public async Task<(TerminalInfo Terminal, string AccessKey)> PairAsync(string machineName, CancellationToken ct = default)
    {
        var terminal = await FindOrCreateAsync(machineName, ct);
        var key = TerminalAccessKey.New();
        var now = clock.GetUtcNow();
        terminal.Pair(TerminalAccessKey.Hash(key), now);
        db.AuditEntries.Add(new AuditEntry(AuditActions.TerminalPaired, null, terminal.Id, terminal.Id, terminal.MachineName, now));
        await db.SaveChangesAsync(ct);
        return (TerminalInfo.From(terminal), key);
    }

    public async Task<TerminalInfo?> FindByAccessKeyAsync(string accessKey, CancellationToken ct = default)
    {
        var hash = TerminalAccessKey.Hash(accessKey);
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.AccessKeyHash == hash && t.IsActive, ct);
        return terminal is null ? null : TerminalInfo.From(terminal);
    }

    public async Task TouchAsync(Guid terminalId, string? appVersion, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.Id == terminalId, ct);
        if (terminal is null) return;
        terminal.Seen(clock.GetUtcNow(), appVersion);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Chaves dos caixas, para preservá-las ao restaurar um backup anterior à conexão do caixa.</summary>
    public async Task<IReadOnlyList<TerminalKey>> ExportKeysAsync(CancellationToken ct = default) =>
        await db.Terminals.AsNoTracking().Where(t => t.AccessKeyHash != null && t.IsActive)
            .Select(t => new TerminalKey(t.Id, t.StoreId, t.Code, t.MachineName, t.AccessKeyHash!, t.PairedAt)).ToListAsync(ct);

    public async Task ImportKeysAsync(IReadOnlyList<TerminalKey> keys, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        foreach (var key in keys)
        {
            var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.Id == key.Id, ct)
                ?? await db.Terminals.SingleOrDefaultAsync(t => t.MachineName == key.MachineName, ct);
            if (terminal is null)
            {
                // Mesmo ID de antes: o caixa em uso continua reconhecido sem precisar reiniciar.
                var storeId = await db.Stores.AnyAsync(s => s.Id == key.StoreId, ct)
                    ? key.StoreId : await db.Stores.OrderBy(s => s.Id).Select(s => s.Id).FirstAsync(ct);
                var code = await db.Terminals.AnyAsync(t => t.StoreId == storeId && t.Code == key.Code, ct)
                    ? await NextCodeAsync(storeId, ct) : key.Code;
                terminal = Terminal.Recreate(key.Id, storeId, code, key.MachineName);
                db.Terminals.Add(terminal);
            }
            terminal.Pair(key.AccessKeyHash, key.PairedAt ?? now);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<Terminal> FindOrCreateAsync(string machineName, CancellationToken ct)
    {
        machineName = machineName.Trim();
        if (machineName.Length is 0 or > 64) throw new DomainException("Nome do computador inválido.");

        var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.MachineName == machineName, ct);
        if (terminal is not null) return terminal;

        var store = await db.Stores.OrderBy(s => s.Id).FirstAsync(ct);
        terminal = new Terminal(store.Id, await NextCodeAsync(store.Id, ct), machineName);
        db.Terminals.Add(terminal);
        return terminal;
    }

    private async Task<string> NextCodeAsync(Guid storeId, CancellationToken ct)
    {
        var codes = await db.Terminals.Where(t => t.StoreId == storeId).Select(t => t.Code).ToListAsync(ct);
        var next = codes.Select(c => int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1;
        return next.ToString("000", CultureInfo.InvariantCulture);
    }
}

/// <summary>Retaguarda › Sistema: caixas conectados ao servidor.</summary>
public sealed class TerminalAdminService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    public async Task<IReadOnlyList<TerminalListItem>> ListAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        return await db.Terminals.AsNoTracking().OrderBy(t => t.Code)
            .Select(t => new TerminalListItem(t.Id, t.Code, t.MachineName, t.IsActive, t.AccessKeyHash != null, t.PairedAt, t.LastSeenAt, t.AppVersion))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Desconecta outro caixa (computador trocado, roubado ou desativado). Para voltar, ele precisa do código de conexão.
    /// <paramref name="terminalId"/> é o caixa de onde parte o pedido.
    /// </summary>
    public async Task<Result<bool>> RevokeAsync(Guid actorId, Guid targetTerminalId, Guid terminalId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        if (targetTerminalId == terminalId) return Result<bool>.Fail("Não é possível desconectar o caixa em uso.");
        var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.Id == targetTerminalId, ct);
        if (terminal is null) return Result<bool>.Fail("Caixa não encontrado.");
        if (await db.CashSessions.AnyAsync(s => s.TerminalId == targetTerminalId && s.Status == CashSessionStatus.Open, ct))
            return Result<bool>.Fail($"O caixa {terminal.Code} está aberto. Feche o caixa antes de desconectá-lo.");
        terminal.Revoke();
        db.AuditEntries.Add(new AuditEntry(AuditActions.TerminalRevoked, actorId, terminalId, targetTerminalId, terminal.MachineName, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return Result<bool>.Ok(true);
    }

    private async Task RequireAsync(Guid actorId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageSystem, ct))
            throw new DomainException("Seu usuário não tem permissão para administrar o sistema.");
    }
}
