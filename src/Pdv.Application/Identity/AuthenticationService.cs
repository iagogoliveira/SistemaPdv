using Microsoft.EntityFrameworkCore;
using Pdv.Domain.Auditing;
using Pdv.Domain.Identity;

namespace Pdv.Application.Identity;

public sealed record UserSession(Guid UserId, string Username, string DisplayName, IReadOnlyCollection<string> Permissions)
{
    public bool Can(string permission) => Permissions.Contains(permission);
}

public sealed class AuthenticationService(IPdvDbContext db, IPasswordHasher hasher, TimeProvider clock)
{
    public const string AdminRoleName = "Administrador";
    public const int MinPasswordLength = 6;

    public Task<bool> HasAnyUserAsync(CancellationToken ct = default) => db.Users.AnyAsync(ct);

    public async Task<Result<UserSession>> CreateInitialAdminAsync(string username, string displayName, string password, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct)) return Result<UserSession>.Fail("O administrador inicial já foi criado.");
        if (password.Length < MinPasswordLength) return Result<UserSession>.Fail($"A senha deve ter ao menos {MinPasswordLength} caracteres.");

        var role = await db.Roles.SingleAsync(r => r.Name == AdminRoleName, ct);
        var user = new User(username, displayName, hasher.Hash(password), role.Id, clock.GetUtcNow());
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Result<UserSession>.Ok(ToSession(user, role));
    }

    public async Task<Result<UserSession>> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var normalized = User.NormalizeUsername(username);
        var user = await db.Users.Include(u => u.Role).SingleOrDefaultAsync(u => u.Username == normalized, ct);

        if (user is null || !user.IsActive || !hasher.Verify(password, user.PasswordHash))
        {
            db.AuditEntries.Add(new AuditEntry(AuditActions.LoginFailed, user?.Id, null, null, normalized, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return Result<UserSession>.Fail("Usuário ou senha inválidos.");
        }

        db.AuditEntries.Add(new AuditEntry(AuditActions.Login, user.Id, null, null, null, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return Result<UserSession>.Ok(ToSession(user, user.Role!));
    }

    /// <summary>
    /// Liberação por supervisor (desconto, cancelamento): valida usuário/senha e a permissão exigida.
    /// Retorna o ID de quem autorizou, que fica registrado na operação.
    /// </summary>
    public async Task<Result<Guid>> AuthorizeAsync(string username, string password, string permission, CancellationToken ct = default)
    {
        var normalized = User.NormalizeUsername(username);
        var user = await db.Users.Include(u => u.Role).SingleOrDefaultAsync(u => u.Username == normalized, ct);

        if (user is null || !user.IsActive || !hasher.Verify(password, user.PasswordHash))
        {
            db.AuditEntries.Add(new AuditEntry(AuditActions.AuthorizationFailed, user?.Id, null, null, $"{normalized} {permission}", clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Fail("Usuário ou senha do supervisor inválidos.");
        }

        if (!user.Role!.HasPermission(permission))
            return Result<Guid>.Fail($"{user.DisplayName} não tem permissão para esta operação.");

        return Result<Guid>.Ok(user.Id);
    }

    private static UserSession ToSession(User user, Role role) =>
        new(user.Id, user.Username, user.DisplayName, role.Permissions.ToHashSet());
}
