using Mattiverse.Core.Apps;
using Mattiverse.Core.Auth;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Integrations;
using Mattiverse.Core.Jobs;
using Mattiverse.Core.Security;
using Mattiverse.Core.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Mattiverse.Core;

/// <summary>
/// Registers the Mattiverse domain/services. The hosting layer is still responsible for the
/// infrastructure that needs configuration: the <c>MattiverseDbContext</c> (connection string),
/// Data Protection, options binding, <c>AppPaths</c> and <c>ICurrentUserAccessor</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMattiverseCore(this IServiceCollection services)
    {
        // Stateless helpers and singletons.
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<JsonConfigStore>();
        services.AddSingleton<SystemConfigService>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<DockerCli>();
        services.AddSingleton<DockerService>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<JobLogStore>();
        services.AddHttpClient();

        // Per-request services (depend on the scoped DbContext).
        services.AddScoped<AuthService>();
        services.AddScoped<SessionService>();
        services.AddScoped<UserService>();
        services.AddScoped<ComposeFiles>();
        services.AddScoped<ComposeRunner>();
        services.AddScoped<PortPlanner>();
        services.AddScoped<AppService>();
        services.AddScoped<BackupService>();
        services.AddScoped<JobService>();
        services.AddScoped<MatcadClient>();
        services.AddScoped<ProxyPublisher>();
        services.AddScoped<IAppProvisioner, MatcadProvisioner>();

        return services;
    }

    /// <summary>The background workers: the job runner and the backup scheduler.</summary>
    public static IServiceCollection AddMattiverseWorkers(this IServiceCollection services)
    {
        services.AddHostedService<JobRunnerService>();
        services.AddHostedService<BackupSchedulerService>();
        return services;
    }
}
