using Microsoft.EntityFrameworkCore;

namespace Pdv.Application.Identity;

/// <summary>
/// Verificação de permissão sempre feita no backend, a partir do banco — nunca a partir
/// de uma lista de permissões enviada pela interface.
/// </summary>
public sealed class PermissionChecker(IPdvDbContext db)
{
    public Task<bool> HasAsync(Guid userId, string permission, CancellationToken ct = default) =>
        db.Users.Where(u => u.Id == userId && u.IsActive)
            .Join(db.Roles, u => u.RoleId, r => r.Id, (u, r) => r)
            .AnyAsync(r => r.Permissions.Contains(permission), ct);
}
