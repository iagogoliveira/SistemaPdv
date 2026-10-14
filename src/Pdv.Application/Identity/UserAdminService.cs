using Microsoft.EntityFrameworkCore;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;

namespace Pdv.Application.Identity;

public sealed record UserListItem(Guid Id, string Username, string DisplayName, Guid RoleId, string RoleName, bool IsActive);

public sealed record RoleListItem(Guid Id, string Name);

public sealed class UserAdminService(IPdvDbContext db, IPasswordHasher hasher, PermissionChecker permissions, TimeProvider clock)
{
    public async Task<IReadOnlyList<UserListItem>> ListAsync(CancellationToken ct = default) =>
        await db.Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserListItem(u.Id, u.Username, u.DisplayName, u.RoleId, u.Role!.Name, u.IsActive))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RoleListItem>> ListRolesAsync(CancellationToken ct = default) =>
        await db.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => new RoleListItem(r.Id, r.Name)).ToListAsync(ct);

    public Task<Result<Guid>> CreateAsync(Guid actorId, string username, string displayName, string password, Guid roleId,
        CancellationToken ct = default) =>
        RunAsync(actorId, "user.created", async () =>
        {
            ValidatePassword(password);
            var normalized = User.NormalizeUsername(username);
            if (await db.Users.AnyAsync(u => u.Username == normalized, ct)) throw new DomainException("Já existe um usuário com esse login.");
            if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct)) throw new DomainException("Perfil inválido.");

            var user = new User(username, displayName, hasher.Hash(password), roleId, clock.GetUtcNow());
            db.Users.Add(user);
            return user.Id;
        }, ct);

    public Task<Result<Guid>> ResetPasswordAsync(Guid actorId, Guid userId, string newPassword, CancellationToken ct = default) =>
        RunAsync(actorId, "user.password_reset", async () =>
        {
            ValidatePassword(newPassword);
            var user = await LoadAsync(userId, ct);
            user.ChangePasswordHash(hasher.Hash(newPassword));
            return user.Id;
        }, ct);

    public Task<Result<Guid>> ChangeRoleAsync(Guid actorId, Guid userId, Guid roleId, CancellationToken ct = default) =>
        RunAsync(actorId, "user.role_changed", async () =>
        {
            var user = await LoadAsync(userId, ct);
            if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct)) throw new DomainException("Perfil inválido.");
            if (user.Id == actorId) throw new DomainException("Você não pode alterar o próprio perfil.");
            user.ChangeRole(roleId);
            return user.Id;
        }, ct);

    public Task<Result<Guid>> SetActiveAsync(Guid actorId, Guid userId, bool active, CancellationToken ct = default) =>
        RunAsync(actorId, active ? "user.activated" : "user.deactivated", async () =>
        {
            var user = await LoadAsync(userId, ct);
            if (!active && user.Id == actorId) throw new DomainException("Você não pode desativar o próprio usuário.");
            if (active) user.Activate(); else user.Deactivate();
            return user.Id;
        }, ct);

    private static void ValidatePassword(string password)
    {
        if (password.Length < AuthenticationService.MinPasswordLength)
            throw new DomainException($"A senha deve ter ao menos {AuthenticationService.MinPasswordLength} caracteres.");
    }

    private async Task<User> LoadAsync(Guid userId, CancellationToken ct) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new DomainException("Usuário não encontrado.");

    private async Task<Result<Guid>> RunAsync(Guid actorId, string action, Func<Task<Guid>> operation, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageUsers, ct))
            return Result<Guid>.Fail("Usuário sem permissão para gerenciar usuários.");
        try
        {
            var id = await operation();
            db.AuditEntries.Add(new AuditEntry(action, actorId, null, id, null, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Ok(id);
        }
        catch (DomainException ex)
        {
            db.ResetChangeTracker();
            return Result<Guid>.Fail(ex.Message);
        }
    }
}
