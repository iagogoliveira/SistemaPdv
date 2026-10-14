using Pdv.Domain.Common;

namespace Pdv.Domain.Identity;

public static class Permissions
{
    public const string ApplyDiscount = "sales.discount";
    public const string CancelSale = "sales.cancel";
    public const string ManageCash = "cash.manage";
    public const string ManageProducts = "catalog.manage";
    public const string AdjustStock = "inventory.adjust";
    public const string ManageUsers = "users.manage";
    public const string ViewReports = "reports.view";
    public const string ManageCompany = "company.manage";

    public static readonly IReadOnlyList<string> All =
        [ApplyDiscount, CancelSale, ManageCash, ManageProducts, AdjustStock, ManageUsers, ViewReports, ManageCompany];
}

public class Role : Entity
{
    private Role() { Name = null!; }

    public Role(string name, IEnumerable<string> permissions)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Nome do perfil é obrigatório.");
        Name = name.Trim();
        Permissions = permissions.Distinct().ToList();
    }

    public string Name { get; private set; }
    public List<string> Permissions { get; private set; } = [];

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    /// <summary>Usado para manter o Administrador com todas as permissões quando surgem novas.</summary>
    public void GrantMissing(IEnumerable<string> permissions)
    {
        foreach (var p in permissions)
            if (!Permissions.Contains(p)) Permissions.Add(p);
    }
}

public class User : Entity
{
    private User() { Username = DisplayName = PasswordHash = null!; }

    public User(string username, string displayName, string passwordHash, Guid roleId, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new DomainException("Usuário é obrigatório.");
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new DomainException("Senha é obrigatória.");
        Username = NormalizeUsername(username);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Username : displayName.Trim();
        PasswordHash = passwordHash;
        RoleId = roleId;
        CreatedAt = createdAt;
        IsActive = true;
    }

    public string Username { get; private set; }
    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public Guid RoleId { get; private set; }
    public Role? Role { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    public void ChangePasswordHash(string passwordHash) => PasswordHash = passwordHash;

    public void ChangeRole(Guid roleId) => RoleId = roleId;

    // Usuários com histórico não são excluídos, apenas desativados.
    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
