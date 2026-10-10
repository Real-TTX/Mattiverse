namespace Mattiverse.Core.Entities;

/// <summary>A local application user (Microsoft Entra ID comes later).</summary>
public class User : AuditableEntity
{
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash produced by <c>IPasswordHasher</c> (never the plain password).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsActive { get; set; } = true;

    /// <summary>Forces a password change on next login (e.g. for a seeded admin).</summary>
    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Preferred theme: system, light or dark (stored in the browser as well, so it applies before login).</summary>
    public string Theme { get; set; } = "system";

    public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
}
