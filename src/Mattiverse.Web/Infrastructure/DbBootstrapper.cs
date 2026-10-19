using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mattiverse.Web.Infrastructure;

/// <summary>
/// Removes a migration lock left behind by a stopped process, applies EF Core migrations and seeds the first administrator on an empty database
/// (headless setups only).
/// </summary>
public static class DbBootstrapper
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<MattiverseDbContext>();
        if (await MigrationLock.ReleaseStaleAsync(db.Database))
        {
            logger.LogWarning("Removed a migration lock left behind by a process that was stopped while it migrated the database.");
        }

        await db.Database.MigrateAsync();

        var users = provider.GetRequiredService<UserService>();
        var options = provider.GetRequiredService<IOptions<MattiverseOptions>>().Value;
        var environment = provider.GetRequiredService<IHostEnvironment>();

        // Seed the initial administrator only on a fresh database AND only when explicitly enabled
        // (headless/automated deployments). Otherwise the first-run setup page creates the admin.
        if (options.Admin.Seed && await users.CountAsync() == 0)
        {
            var admin = options.Admin;
            await users.CreateAsync(admin.Username, admin.DisplayName, admin.Password, UserRole.Admin, mustChangePassword: true);
            logger.LogWarning(
                "Seeded initial administrator '{Username}'. The initial password must be changed on first login.",
                admin.Username);
        }

        // Local/dev only (never in Production): a ready-to-use test account so testing never touches
        // the real admin. Automatic, no configuration flag needed.
        if (environment.IsDevelopment() && !await users.UsernameExistsAsync("tester"))
        {
            await users.CreateAsync("tester", "Test User", "Tester123!", UserRole.Admin, mustChangePassword: false);
            logger.LogWarning("Seeded local test account 'tester' / 'Tester123!' (Development only).");
        }
    }
}
