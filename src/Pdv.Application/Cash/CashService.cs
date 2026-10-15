using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Cash;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;

namespace Pdv.Application.Cash;

public sealed record CashSessionSummary(Guid SessionId, DateTimeOffset OpenedAt, decimal ExpectedCash, CashSessionStatus Status,
    decimal? CountedAmount, decimal? Difference);

public sealed class CashService(IPdvDbContext db, PermissionChecker permissions, Licensing.ILicenseGuard license, TimeProvider clock)
{
    public async Task<CashSessionSummary?> GetOpenSessionAsync(Guid terminalId, CancellationToken ct = default)
    {
        var session = await LoadOpenAsync(terminalId, ct);
        return session is null ? null : ToSummary(session);
    }

    public async Task<Result<CashSessionSummary>> OpenAsync(Guid terminalId, Guid userId, decimal openingAmount, CancellationToken ct = default)
    {
        if (await db.CashSessions.AnyAsync(s => s.TerminalId == terminalId && s.Status == CashSessionStatus.Open, ct))
            return Result<CashSessionSummary>.Fail("Já existe um caixa aberto neste terminal.");
        try { await license.EnsureCanSellAsync(ct); }
        catch (DomainException ex) { return Result<CashSessionSummary>.Fail(ex.Message); }

        return await RunAsync(() =>
        {
            var session = CashSession.Open(terminalId, userId, openingAmount, clock.GetUtcNow());
            db.CashSessions.Add(session);
            Audit(AuditActions.CashOpened, userId, terminalId, session.Id, $"Fundo: {openingAmount:N2}");
            return Task.FromResult(session);
        }, ct);
    }

    public Task<Result<CashSessionSummary>> SupplyAsync(Guid terminalId, Guid userId, decimal amount, string? reason,
        CancellationToken ct = default) =>
        WithOpenSessionAsync(terminalId, userId, Permissions.ManageCash, session =>
        {
            session.RegisterSupply(amount, reason, userId, clock.GetUtcNow());
            Audit(AuditActions.CashSupply, userId, terminalId, session.Id, $"{amount:N2} {reason}");
        }, ct);

    /// <param name="userId">Quem autorizou a sangria (operador com permissão ou supervisor).</param>
    public Task<Result<CashSessionSummary>> WithdrawAsync(Guid terminalId, Guid userId, decimal amount, string reason,
        CancellationToken ct = default) =>
        WithOpenSessionAsync(terminalId, userId, Permissions.ManageCash, session =>
        {
            session.RegisterWithdrawal(amount, reason, userId, clock.GetUtcNow());
            Audit(AuditActions.CashWithdrawal, userId, terminalId, session.Id, $"{amount:N2} {reason}");
        }, ct);

    public Task<Result<CashSessionSummary>> CloseAsync(Guid terminalId, Guid userId, decimal countedAmount, CancellationToken ct = default) =>
        WithOpenSessionAsync(terminalId, userId, null, session =>
        {
            session.Close(countedAmount, userId, clock.GetUtcNow());
            Audit(AuditActions.CashClosed, userId, terminalId, session.Id,
                $"Esperado {session.ExpectedAmount:N2}, contado {session.CountedAmount:N2}, diferença {session.Difference:N2}");
        }, ct);

    private async Task<Result<CashSessionSummary>> WithOpenSessionAsync(Guid terminalId, Guid userId, string? requiredPermission,
        Action<CashSession> action, CancellationToken ct)
    {
        if (requiredPermission is not null && !await permissions.HasAsync(userId, requiredPermission, ct))
            return Result<CashSessionSummary>.Fail("Usuário sem permissão para movimentar o caixa.");

        var session = await LoadOpenAsync(terminalId, ct);
        if (session is null) return Result<CashSessionSummary>.Fail("Não há caixa aberto neste terminal.");

        return await RunAsync(() => { action(session); return Task.FromResult(session); }, ct);
    }

    private async Task<Result<CashSessionSummary>> RunAsync(Func<Task<CashSession>> action, CancellationToken ct)
    {
        try
        {
            var session = await action();
            await db.SaveChangesAsync(ct);
            return Result<CashSessionSummary>.Ok(ToSummary(session));
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<CashSessionSummary>.Fail(ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ResetChangeTracker();
            return Result<CashSessionSummary>.Fail("O caixa foi alterado por outra operação. Tente novamente.");
        }
    }

    private Task<CashSession?> LoadOpenAsync(Guid terminalId, CancellationToken ct) =>
        db.CashSessions.Include(s => s.Movements)
            .SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == CashSessionStatus.Open, ct);

    private void Audit(string action, Guid userId, Guid terminalId, Guid referenceId, string? details) =>
        db.AuditEntries.Add(new AuditEntry(action, userId, terminalId, referenceId, details, clock.GetUtcNow()));

    private static CashSessionSummary ToSummary(CashSession s) =>
        new(s.Id, s.OpenedAt, s.ExpectedCash, s.Status, s.CountedAmount, s.Difference);
}
