using System.Text.RegularExpressions;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Integrations;
using Mattiverse.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mattiverse.Core.Apps;

/// <summary>The answers of the install form.</summary>
public sealed record InstallRequest(string CatalogId, string? Slug, string? Name, IReadOnlyDictionary<string, string?> Values);

/// <summary>Result of the synchronous first part of an install: the new app, or the problems with the input.</summary>
public sealed record PrepareResult(InstalledApp? App, IReadOnlyList<string> Errors, IReadOnlyList<PortConflict> PortConflicts)
{
    public bool Succeeded => App is not null;
}

/// <summary>
/// Installs, updates, removes and controls the apps. Each operation is split the way the UI needs it: the quick part that
/// validates input and records the app runs in the web request; the slow Docker part (<c>Run...Async</c>) runs as a background job.
/// </summary>
public sealed partial class AppService
{
    private readonly MattiverseDbContext _db;
    private readonly CatalogService _catalog;
    private readonly ComposeFiles _files;
    private readonly ComposeRunner _compose;
    private readonly DockerService _docker;
    private readonly PortPlanner _ports;
    private readonly ISecretProtector _protector;
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly BackupService _backups;
    private readonly IEnumerable<IAppProvisioner> _provisioners;
    private readonly ILogger<AppService> _logger;

    public AppService(
        MattiverseDbContext db,
        CatalogService catalog,
        ComposeFiles files,
        ComposeRunner compose,
        DockerService docker,
        PortPlanner ports,
        ISecretProtector protector,
        SystemConfigService config,
        ProxyPublisher proxy,
        BackupService backups,
        IEnumerable<IAppProvisioner> provisioners,
        ILogger<AppService> logger)
    {
        _db = db;
        _catalog = catalog;
        _files = files;
        _compose = compose;
        _docker = docker;
        _ports = ports;
        _protector = protector;
        _config = config;
        _proxy = proxy;
        _backups = backups;
        _provisioners = provisioners;
        _logger = logger;
    }

    // ------------------------------------------------------------------ queries

    public Task<List<InstalledApp>> ListAsync(CancellationToken ct = default)
        => _db.InstalledApps.AsNoTracking().OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync(ct);

    public Task<InstalledApp?> GetAsync(long id, CancellationToken ct = default)
        => _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<InstalledApp?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => _db.InstalledApps.FirstOrDefaultAsync(a => a.Slug == slug, ct);

    /// <summary>The live state of all apps from one <c>docker ps</c> call, keyed by slug.</summary>
    public async Task<Dictionary<string, RuntimeInfo>> GetRuntimesAsync(CancellationToken ct = default)
    {
        var containers = await _docker.ListContainersAsync(ct: ct);
        return containers
            .GroupBy(c => c.Instance)
            .ToDictionary(g => g.Key, g => DockerService.Summarize(g.ToList()));
    }

    public async Task<RuntimeInfo> GetRuntimeAsync(string slug, CancellationToken ct = default)
        => DockerService.Summarize(await _docker.ListContainersAsync(slug, ct));

    // ------------------------------------------------------------------ install

    /// <summary>Validates the form, records the app (state "installing") and writes its files. The containers are started by <see cref="RunInstallAsync"/>.</summary>
    public async Task<PrepareResult> PrepareInstallAsync(InstallRequest request, CancellationToken ct = default)
    {
        var catalog = _catalog.Get(request.CatalogId);
        if (catalog is null)
        {
            return new PrepareResult(null, [$"The app '{request.CatalogId}' is not in the catalog."], []);
        }

        var errors = new List<string>();
        var slug = await UniqueSlugAsync(string.IsNullOrWhiteSpace(request.Slug) ? catalog.Id : request.Slug, ct);
        if (slug is null)
        {
            errors.Add("The name may only contain lowercase letters, digits and dashes.");
        }

        var variables = AppVariables.Resolve(catalog, request.Values, existing: null, errors);
        if (errors.Count > 0)
        {
            return new PrepareResult(null, errors, []);
        }

        var config = await _config.GetAsync(ct);
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? (slug == catalog.Id ? catalog.Name : $"{catalog.Name} ({slug})")
            : request.Name.Trim();

        var app = new InstalledApp
        {
            Slug = slug!,
            CatalogId = catalog.Id,
            Name = name,
            Role = catalog.Role,
            ProjectName = _files.ProjectName(slug!),
            UiService = catalog.Ui?.Service,
            UiPort = catalog.Ui?.Port,
            HostPort = await _ports.PlanHostPortAsync(catalog, ProxyPublisher.SelfPort(), ct),
            UiPath = catalog.Ui?.Path,
            Lifecycle = AppLifecycle.Installing,
            VariablesJson = AppVariables.Serialize(catalog, variables, _protector),
            CredentialsJson = AppCredentials.Serialize(AppCredentials.Build(catalog, variables), _protector),
            SortOrder = await _db.InstalledApps.CountAsync(ct),
        };

        _db.InstalledApps.Add(app);
        await _db.SaveChangesAsync(ct);
        await _files.WriteAsync(app, catalog, variables, config.PublicHost ?? "localhost", ct);

        var conflicts = await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct);
        return new PrepareResult(app, [], conflicts);
    }

    /// <summary>Pulls the images, starts the app, waits until it runs and sets it up (the job part of an install).</summary>
    public async Task RunInstallAsync(long appId, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var catalog = _catalog.Get(app.CatalogId) ?? throw new InvalidOperationException($"The catalog entry '{app.CatalogId}' no longer exists.");

        try
        {
            await _docker.EnsureNetworkAsync(ct);

            var variables = AppVariables.Deserialize(catalog, app.VariablesJson, _protector);
            foreach (var conflict in await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct))
            {
                log($"Warning: host port {conflict.Port}/{conflict.Protocol} is already used by '{conflict.Container}'; the start may fail.");
            }

            await ComposeStepAsync(app.Slug, ["config", "--quiet"], "The compose file is invalid", log, ct);
            log("Pulling the images ...");
            await ComposeStepAsync(app.Slug, ["pull"], "The images could not be pulled (a private image needs 'docker login' on the host)", log, ct);
            log("Starting the app ...");
            await ComposeStepAsync(app.Slug, ["up", "-d", "--remove-orphans"], "The app could not be started", log, ct);

            await WaitUntilRunningAsync(app.Slug, log, ct);

            app.Lifecycle = AppLifecycle.Installed;
            app.StatusMessage = null;
            await _db.SaveChangesAsync(ct);
            log($"{app.Name} is installed.");

            await ProvisionAsync(app, catalog, log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Lifecycle = AppLifecycle.Failed;
            app.StatusMessage = Truncate(ex.Message, 900);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    // ------------------------------------------------------------------ update

    /// <summary>
    /// Updates an app: optional backup first, the compose files are re-written from the current catalog entry, the images are
    /// pulled and the containers are only re-created when an image actually changed.
    /// </summary>
    public async Task RunUpdateAsync(long appId, bool backupFirst, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var catalog = _catalog.Get(app.CatalogId) ?? throw new InvalidOperationException($"The catalog entry '{app.CatalogId}' no longer exists.");
        var config = await _config.GetAsync(ct);

        if (backupFirst)
        {
            log("Backing up before the update ...");
            await _backups.RunBackupAsync(app, BackupKind.PreUpdate, config.Backups.StopContainers, "Before an update", log, ct);
        }

        // New catalog versions can add variables (a database password): keep the answers, fill in what is new.
        var errors = new List<string>();
        var existing = AppVariables.Deserialize(catalog, app.VariablesJson, _protector);
        var variables = AppVariables.Resolve(catalog, new Dictionary<string, string?>(), existing, errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', errors));
        }

        app.VariablesJson = AppVariables.Serialize(catalog, variables, _protector);
        await _files.WriteAsync(app, catalog, variables, config.PublicHost ?? "localhost", ct);
        await _db.SaveChangesAsync(ct);

        await _docker.EnsureNetworkAsync(ct);
        var images = await _compose.GetImagesAsync(app.Slug, ct);
        var before = await ImageIdsAsync(images, ct);

        log("Pulling the images ...");
        await ComposeStepAsync(app.Slug, ["pull"], "The images could not be pulled", log, ct);
        var after = await ImageIdsAsync(images, ct);

        var changed = images.Where(i => before.GetValueOrDefault(i) != after.GetValueOrDefault(i)).ToList();
        var runtime = await GetRuntimeAsync(app.Slug, ct);
        if (changed.Count == 0 && runtime.State == RuntimeState.Running)
        {
            log("Already up to date: no newer images.");
            return;
        }

        foreach (var image in changed)
        {
            log($"New image: {image}");
        }

        log("Re-creating the containers ...");
        await ComposeStepAsync(app.Slug, ["up", "-d", "--remove-orphans"], "The app could not be started", log, ct);
        await WaitUntilRunningAsync(app.Slug, log, ct);

        app.LastUpdatedAt = DateTime.UtcNow;
        app.Lifecycle = AppLifecycle.Installed;
        app.StatusMessage = null;
        await _db.SaveChangesAsync(ct);
        log($"{app.Name} is up to date.");
    }

    private async Task<Dictionary<string, string?>> ImageIdsAsync(IEnumerable<string> images, CancellationToken ct)
    {
        var ids = new Dictionary<string, string?>();
        foreach (var image in images)
        {
            ids[image] = await _docker.GetImageIdAsync(image, ct);
        }

        return ids;
    }

    // ------------------------------------------------------------------ uninstall

    public async Task RunUninstallAsync(long appId, bool deleteData, bool backupFirst, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var config = await _config.GetAsync(ct);

        app.Lifecycle = AppLifecycle.Removing;
        await _db.SaveChangesAsync(ct);

        try
        {
            if (backupFirst && _files.Exists(app.Slug))
            {
                log("Saving a final backup ...");
                await _backups.RunBackupAsync(app, BackupKind.Manual, config.Backups.StopContainers, "Final backup before the uninstall", log, ct);
            }

            await _proxy.UnpublishAsync(app, log, ct);

            if (_files.Exists(app.Slug))
            {
                log(deleteData ? "Removing the containers and the data ..." : "Removing the containers (the data is kept) ...");
                var down = deleteData ? new[] { "down", "--remove-orphans", "--volumes" } : new[] { "down", "--remove-orphans" };
                await ComposeStepAsync(app.Slug, down, "The app could not be removed", log, ct);
            }

            if (deleteData)
            {
                _files.DeleteFolder(app.Slug);
            }

            var shares = await _db.ShareLinks.Where(s => s.AppId == app.Id).ToListAsync(ct);
            _db.ShareLinks.RemoveRange(shares);
            _db.InstalledApps.Remove(app);
            await _db.SaveChangesAsync(ct);
            log($"{app.Name} was removed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Lifecycle = AppLifecycle.Failed;
            app.StatusMessage = Truncate("Uninstall failed: " + ex.Message, 900);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    // ------------------------------------------------------------------ start / stop / restart

    /// <summary>Runs <c>start</c>, <c>stop</c> or <c>restart</c> on all containers of the app.</summary>
    public async Task RunLifecycleAsync(long appId, string command, Action<string> log, CancellationToken ct = default)
    {
        if (command is not ("start" or "stop" or "restart"))
        {
            throw new ArgumentException("Unknown command: " + command, nameof(command));
        }

        var app = await RequireAsync(appId, ct);
        await ComposeStepAsync(app.Slug, [command], $"Could not {command} the app", log, ct);
        log(command switch { "start" => "Started.", "stop" => "Stopped.", _ => "Restarted." });
    }

    // ------------------------------------------------------------------ helpers

    private async Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        try
        {
            var provisioner = _provisioners.FirstOrDefault(p => p.Key == catalog.Provision);
            if (provisioner is not null)
            {
                log($"Setting up {app.Name} ...");
                await provisioner.ProvisionAsync(app, catalog, log, ct);
            }

            // An app that is not a proxy itself gets its route right away when a proxy with a base domain exists.
            if (catalog.Role != AppRoles.Proxy)
            {
                await _proxy.PublishAsync(app, log, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The app runs; only the integration failed. Say so, but do not fail the install.
            _logger.LogWarning(ex, "Provisioning {Slug} failed.", app.Slug);
            log($"Warning: the follow-up setup failed: {ex.Message}");
        }
    }

    private async Task ComposeStepAsync(string slug, IEnumerable<string> subcommand, string failureMessage, Action<string> log, CancellationToken ct)
    {
        var args = subcommand.ToList();
        var tail = new Queue<string>();
        var exit = await _compose.StreamAsync(slug, args, line =>
        {
            log(line);
            tail.Enqueue(line);
            if (tail.Count > 6)
            {
                tail.Dequeue();
            }
        }, ct);

        if (exit != 0)
        {
            var detail = string.Join(" | ", tail.Where(l => !string.IsNullOrWhiteSpace(l)));
            throw new InvalidOperationException($"{failureMessage} (docker compose {args[0]} exit code {exit}): {detail}");
        }
    }

    /// <summary>
    /// Waits until every container runs (a container that finished with exit code 0 is a one-shot task and counts as done).
    /// A container that exits with an error or restarts in a loop fails the install with its last log lines.
    /// </summary>
    private async Task WaitUntilRunningAsync(string slug, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            var containers = await _docker.ListContainersAsync(slug, ct);
            foreach (var container in containers)
            {
                var failed = container.State.Equals("dead", StringComparison.OrdinalIgnoreCase) ||
                    (container.State.Equals("exited", StringComparison.OrdinalIgnoreCase) && !container.Status.Contains("(0)", StringComparison.Ordinal));
                if (failed || container.State.Equals("restarting", StringComparison.OrdinalIgnoreCase))
                {
                    var logs = await _docker.GetLogsAsync(container.Name, 12, ct);
                    throw new InvalidOperationException($"The container '{container.Service}' does not stay up ({container.Status}). Last log lines: {logs.ReplaceLineEndings(" | ")}");
                }
            }

            var settled = containers.Count > 0 && containers.All(c =>
                (c.IsRunning && !c.Status.Contains("health: starting", StringComparison.OrdinalIgnoreCase)) ||
                (c.State.Equals("exited", StringComparison.OrdinalIgnoreCase) && c.Status.Contains("(0)", StringComparison.Ordinal)));
            stable = settled ? stable + 1 : 0;
            if (stable >= 2)
            {
                log("All containers are running.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        log("Warning: the containers are still starting after 90 seconds. Check the logs of the app.");
    }

    private async Task<InstalledApp> RequireAsync(long appId, CancellationToken ct)
        => await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == appId, ct)
           ?? throw new InvalidOperationException("The app does not exist (any more).");

    /// <summary>Normalizes a name to a slug and appends -2, -3, ... until it is free. Returns <c>null</c> for an unusable name.</summary>
    private async Task<string?> UniqueSlugAsync(string wanted, CancellationToken ct)
    {
        var baseSlug = Slugify(wanted);
        if (baseSlug is null)
        {
            return null;
        }

        var taken = (await _db.InstalledApps.Select(a => a.Slug).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var slug = baseSlug;
        for (var n = 2; taken.Contains(slug); n++)
        {
            slug = $"{baseSlug}-{n}";
        }

        return slug;
    }

    public static string? Slugify(string value)
    {
        var slug = SlugCleanup().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length is >= 1 and <= 40 && char.IsLetterOrDigit(slug[0]) ? slug : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugCleanup();
}
