using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Core.Users;

/// <summary>CRUD and password management for local users.</summary>
public sealed class UserService
{
    private readonly MattiverseDbContext _db;
    private readonly IPasswordHasher _passwordHasher;

    public UserService(MattiverseDbContext db, IPasswordHasher passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public Task<List<User>> GetAllAsync(CancellationToken ct = default)
        => _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);

    public Task<User?> GetAsync(long id, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<bool> UsernameExistsAsync(string username, long? excludeId = null, CancellationToken ct = default)
        => _db.Users.AnyAsync(u => u.Username == username && (excludeId == null || u.Id != excludeId), ct);

    public async Task<User> CreateAsync(string username, string displayName, string password, UserRole role, bool mustChangePassword = false, CancellationToken ct = default)
    {
        var user = new User
        {
            Username = username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            PasswordHash = _passwordHasher.Hash(password),
            Role = role,
            IsActive = true,
            MustChangePassword = mustChangePassword
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<bool> UpdateAsync(long id, string displayName, UserRole role, bool isActive, string? newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? user.Username : displayName.Trim();
        user.Role = role;
        user.IsActive = isActive;

        if (!string.IsNullOrEmpty(newPassword))
        {
            user.PasswordHash = _passwordHasher.Hash(newPassword);
            user.MustChangePassword = false;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        _db.Users.Remove(user); // soft delete
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sets a new password and clears the "must change" flag.</summary>
    public async Task<bool> ChangePasswordAsync(long id, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = _passwordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Stores the preferred theme (<c>system</c>, <c>light</c> or <c>dark</c>) of a user. Returns the user, or <c>null</c> if it does not exist.</summary>
    public async Task<User?> SetThemeAsync(long id, string theme, CancellationToken ct = default)
    {
        if (theme is not ("system" or "light" or "dark"))
        {
            throw new ArgumentException("Unknown theme: " + theme, nameof(theme));
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return null;
        }

        user.Theme = theme;
        await _db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// The local account of a person who signed in through MatIdentity: found by the claim <c>sub</c>, else the local account with the same user name that is
    /// not linked yet (the administrator who set Mattiverse up is the administrator of MatIdentity too), else a new account. The role follows the claim
    /// <c>role</c> of the token (<c>admin</c> is Admin, anything else User) when it is there. Null for a disabled account.
    /// </summary>
    public async Task<User?> FindOrCreateExternalAsync(string subject, string username, string? displayName, string? role, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == subject, ct)
                   ?? await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.ExternalSubject == null, ct);
        if (user is null)
        {
            var name = username;
            for (var n = 2; await _db.Users.AnyAsync(u => u.Username == name, ct); n++)
            {
                name = $"{username}-{n}";
            }

            // No password: the account signs in through MatIdentity (a local password can be set by an administrator).
            user = new User { Username = name, DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(), PasswordHash = string.Empty, Role = UserRole.User, IsActive = true };
            _db.Users.Add(user);
        }

        if (!user.IsActive)
        {
            return null;
        }

        user.ExternalSubject = subject;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            user.DisplayName = displayName.Trim();
        }

        if (role is not null)
        {
            user.Role = role == "admin" ? UserRole.Admin : UserRole.User;
        }

        user.MustChangePassword = false;
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public Task<int> CountAsync(CancellationToken ct = default)
        => _db.Users.CountAsync(ct);
}
