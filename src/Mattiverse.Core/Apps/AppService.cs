using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Engines;
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
/// The stack itself lives in an engine (MatDock, or Mattiverse's own compose runner), see <see cref="IStackEngine"/>.
/// </summary>
public sealed partial class AppService
{
    private readonly MattiverseDbContext _db;
    private readonly CatalogService _catalog;
    private readonly ComposeFiles _files;
    private readonly DockerService _docker;
    private readonly PortPlanner _ports;
    private readonly ISecretProtector _protector;
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly DnsPublisher _dns;
    private readonly StackEngines _engines;
    private readonly BackupHub _backups;
    private readonly IEnumerable<IAppProvisioner> _provisioners;
    private readonly IdentityLink _identity;
    private readonly MailLink _mail;
    private readonly ILogger<AppService> _logger;

    public AppService(
        MattiverseDbContext db,
        CatalogService catalog,
        ComposeFiles files,
        DockerService docker,
        PortPlanner ports,
        ISecretProtector protector,
        SystemConfigService config,
        ProxyPublisher proxy,
        DnsPublisher dns,
        StackEngines engines,
        BackupHub backups,
        IEnumerable<IAppProvisioner> provisioners,
        IdentityLink identity,
        MailLink mail,
        ILogger<AppService> logger)
    {
        _db = db;
        _catalog = catalog;
        _files = files;
        _docker = docker;
        _ports = ports;
        _protector = protector;
        _config = config;
        _proxy = proxy;
        _dns = dns;
        _engines = engines;
        _backups = backups;
        _provisioners = provisioners;
        _identity = identity;
        _mail = mail;
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

    /// <summary>The stack as its engine has it now (compose and env), for the Files tab.</summary>
    public Task<StackSpec?> ReadStackAsync(InstalledApp app, CancellationToken ct = default)
        => _engines.For(app).ReadAsync(app, ct);

    // ------------------------------------------------------------------ install

    /// <summary>Validates the form and records the app (state "installing"). The stack is deployed by <see cref="RunInstallAsync"/>.</summary>
    public async Task<PrepareResult> PrepareInstallAsync(InstallRequest request, CancellationToken ct = default)
    {
        var catalog = _catalog.Get(request.CatalogId);
        if (catalog is null)
        {
            return new PrepareResult(null, [$"The app '{request.CatalogId}' is not in the catalog."], []);
        }

        // An engine (core) exists once: Mattiverse finds it by its catalog id. A second install would be a second MatDock or MatSMTP that nothing talks to;
        // a failed one is tried again on its own page instead.
        if (catalog.Core && await _db.InstalledApps.AnyAsync(a => a.CatalogId == catalog.Id, ct))
        {
            return new PrepareResult(null, [$"{catalog.Name} is installed already. If its install failed, use \"Try again\" on its page."], []);
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

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? (slug == catalog.Id ? catalog.Name : $"{catalog.Name} ({slug})")
            : request.Name.Trim();

        var app = new InstalledApp
        {
            Slug = slug!,
            CatalogId = catalog.Id,
            Name = name,
            Role = catalog.Role,
            IsCore = catalog.Core,
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

        var conflicts = await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct);
        return new PrepareResult(app, [], conflicts);
    }

    /// <summary>Deploys the stack, waits until it runs and sets it up (the job part of an install).</summary>
    public async Task RunInstallAsync(long appId, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var catalog = _catalog.Get(app.CatalogId) ?? throw new InvalidOperationException($"The catalog entry '{app.CatalogId}' no longer exists.");

        try
        {
            await _docker.EnsureNetworkAsync(ct);

            // Chosen now, not when the install was started: the engines are installed one after the other by the wizard.
            var engine = await _engines.ChooseForNewAsync(catalog, ct);
            app.Engine = engine.Id;
            await _db.SaveChangesAsync(ct);
            log(engine.Id == "matdock" ? "MatDock runs this app." : "Mattiverse runs this app itself (docker compose).");

            var (spec, variables) = await BuildSpecAsync(app, catalog, log, ct);
            foreach (var conflict in await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct))
            {
                log($"Warning: host port {conflict.Port}/{conflict.Protocol} is already used by '{conflict.Container}'; the start may fail.");
            }

            await engine.DeployAsync(spec, log, ct);
            app.ComposeSha = await StoredHashAsync(engine, app, spec, ct);
            await _db.SaveChangesAsync(ct);

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
    /// Updates an app: optional backup first, then the engine pulls the images and re-creates the containers only when something changed.
    /// A newer catalog entry is applied too, unless the user changed the stack in the engine (in MatDock, for example): then their
    /// version is kept.
    /// </summary>
    public async Task RunUpdateAsync(long appId, bool backupFirst, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var catalog = _catalog.Get(app.CatalogId) ?? throw new InvalidOperationException($"The catalog entry '{app.CatalogId}' no longer exists.");
        var config = await _config.GetAsync(ct);

        if (backupFirst)
        {
            log("Backing up before the update ...");
            await _backups.RunAsync(app, BackupKind.PreUpdate, config.Backups.StopContainers, "Before an update", log, ct);
        }

        await _docker.EnsureNetworkAsync(ct);
        var engine = _engines.For(app);
        var (spec, _) = await BuildSpecAsync(app, catalog, log, ct);

        var current = await engine.ReadAsync(app, ct);
        var customized = current is not null && app.ComposeSha is not null && ComposeHash.Of(current.Compose) != app.ComposeSha;
        if (customized)
        {
            log($"The stack was changed in {(engine.Id == "matdock" ? "MatDock" : "its files")} since Mattiverse deployed it: the update keeps your version.");
        }

        var changed = await engine.UpdateAsync(app, customized ? null : spec, log, ct);
        if (!changed)
        {
            log("Already up to date: no newer images.");
            return;
        }

        if (!customized)
        {
            app.ComposeSha = await StoredHashAsync(engine, app, spec, ct);
        }

        await WaitUntilRunningAsync(app.Slug, log, ct);

        app.LastUpdatedAt = DateTime.UtcNow;
        app.Lifecycle = AppLifecycle.Installed;
        app.StatusMessage = null;
        await _db.SaveChangesAsync(ct);
        log($"{app.Name} is up to date.");
    }

    // ------------------------------------------------------------------ uninstall

    public async Task RunUninstallAsync(long appId, bool deleteData, bool backupFirst, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        // An engine stays; only an install that failed can be removed to start over (the page offers it, the job must allow it too).
        if (app.IsCore && app.Lifecycle != AppLifecycle.Failed)
        {
            throw new InvalidOperationException($"{app.Name} is part of Mattiverse and cannot be removed here.");
        }

        var config = await _config.GetAsync(ct);
        app.Lifecycle = AppLifecycle.Removing;
        await _db.SaveChangesAsync(ct);

        try
        {
            if (backupFirst)
            {
                log("Saving a final backup ...");
                await _backups.RunAsync(app, BackupKind.Manual, config.Backups.StopContainers, "Final backup before the uninstall", log, ct);
            }

            await _proxy.UnpublishAsync(app, log, ct);
            await _dns.PublishAllAsync(log, excludeSlug: app.Slug, ct);
            await RemoveFromIdentityAsync(app, log, ct);
            await RemoveFromMailAsync(app, log, ct);

            log(deleteData ? "Removing the containers and the data ..." : "Removing the containers (the data is kept) ...");
            await _engines.For(app).RemoveAsync(app, deleteData, log, ct);

            var shares = await _db.ShareLinks.Where(s => s.AppId == app.Id).ToListAsync(ct);
            _db.ShareLinks.RemoveRange(shares);
            _db.InstalledApps.Remove(app);
            await _db.SaveChangesAsync(ct);
            log($"{app.Name} was removed.");

            if (app.Role == AppRoles.Identity)
            {
                // Without MatIdentity the apps sign people in themselves again.
                await ConnectIdentityAppsAsync(log, ct);
            }
            else if (app.Role == AppRoles.Mail)
            {
                // Without MatSMTP the apps send their mail themselves again.
                await ConnectMailAppsAsync(log, ct);
            }
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
        if (!Enum.TryParse<StackAction>(command, ignoreCase: true, out var action))
        {
            throw new ArgumentException("Unknown command: " + command, nameof(command));
        }

        var app = await RequireAsync(appId, ct);
        await _engines.For(app).ControlAsync(app, action, log, ct);
        log(action switch { StackAction.Start => "Started.", StackAction.Stop => "Stopped.", _ => "Restarted." });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// What the engine deploys for an app, from its current catalog entry. Keeps the answers the user gave and fills in what is new (a
    /// newer catalog entry can add a variable such as a database password), so fixes to a catalog entry reach apps that were installed earlier.
    /// </summary>
    private async Task<(StackSpec Spec, Dictionary<string, string> Variables)> BuildSpecAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        var errors = new List<string>();
        var existing = AppVariables.Deserialize(catalog, app.VariablesJson, _protector);
        var variables = AppVariables.Resolve(catalog, new Dictionary<string, string?>(), existing, errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', errors));
        }

        // What Mattiverse fills itself: the sign-in through MatIdentity (registered there now), the issuer of MatIdentity.
        try
        {
            await _identity.ApplyAsync(app, catalog, variables, log, ct);
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: MatIdentity could not register the app ({ex.Message}); it keeps its own sign-in for now.");
        }

        // ... and the mail through MatSMTP (a relay user there now).
        try
        {
            await _mail.ApplyAsync(app, catalog, variables, log, ct);
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: MatSMTP could not make the relay user of the app ({ex.Message}); its mail settings stay as they are for now.");
        }

        // A newer catalog entry may make an app an engine (MatIdentity, MatSMTP) or give it another role.
        app.IsCore = catalog.Core;
        app.Role = catalog.Role;

        var config = await _config.GetAsync(ct);
        app.VariablesJson = AppVariables.Serialize(catalog, variables, _protector);
        await _db.SaveChangesAsync(ct);
        return (_files.Build(app, catalog, variables, config.PublicHost ?? "localhost"), variables);
    }

    /// <summary>The hash of the compose text as the engine stores it (an engine may re-format the YAML), falling back to what was sent.</summary>
    private static async Task<string> StoredHashAsync(IStackEngine engine, InstalledApp app, StackSpec sent, CancellationToken ct)
        => ComposeHash.Of((await engine.ReadAsync(app, ct) ?? sent).Compose);

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

            // MatIdentity came: the apps that can sign in through it are registered there and get their sign-in.
            if (catalog.Role == AppRoles.Identity)
            {
                await ConnectIdentityAppsAsync(log, ct);
            }

            // MatSMTP came: the apps that send mail get their relay user and send through it.
            if (catalog.Role == AppRoles.Mail)
            {
                await ConnectMailAppsAsync(log, ct);
            }

            // An app that is not a proxy itself gets its route and its DNS record right away when the engines exist.
            if (catalog.Role != AppRoles.Proxy)
            {
                if (await _proxy.PublishAsync(app, log, ct) is not null)
                {
                    await _dns.PublishAllAsync(log, null, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The app runs; only the integration failed. Say so, but do not fail the install.
            _logger.LogWarning(ex, "Provisioning {Slug} failed.", app.Slug);
            log($"Warning: the follow-up setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers every installed app that can sign in through MatIdentity there (or, without MatIdentity, takes the sign-in away again) and deploys it
    /// again with its new variables. Runs after MatIdentity was installed or removed.
    /// </summary>
    public Task ConnectIdentityAppsAsync(Action<string> log, CancellationToken ct) => ConnectPlatformAppsAsync(identity: true, mail: false, log, ct);

    /// <summary>
    /// Gives every installed app that sends mail its relay user in MatSMTP (or, without MatSMTP, takes its mail variables away again) and deploys it again
    /// with its new variables. Runs after MatSMTP was installed or removed.
    /// </summary>
    public Task ConnectMailAppsAsync(Action<string> log, CancellationToken ct) => ConnectPlatformAppsAsync(identity: false, mail: true, log, ct);

    /// <summary>
    /// Connects the apps to MatIdentity and MatSMTP in one pass (the job that connects the engines): every app that uses one of them is deployed once, with
    /// all its variables filled.
    /// </summary>
    public async Task ConnectPlatformAppsAsync(bool identity, bool mail, Action<string> log, CancellationToken ct)
    {
        if (identity)
        {
            // Mattiverse itself first: its own sign-in goes through MatIdentity too (or no more).
            try
            {
                await _identity.ConnectSelfAsync(origin: null, log, ct);
            }
            catch (InvalidOperationException ex)
            {
                log($"Warning: Mattiverse could not register itself in MatIdentity: {ex.Message}");
            }
        }

        var apps = await _db.InstalledApps.Where(a => a.Lifecycle == AppLifecycle.Installed).ToListAsync(ct);
        foreach (var other in apps)
        {
            var otherCatalog = _catalog.Get(other.CatalogId);
            if (otherCatalog is null || !((identity && otherCatalog.Identity is not null) || (mail && otherCatalog.Mail is not null)))
            {
                continue;
            }

            log($"Connecting {other.Name} ...");
            try
            {
                var (spec, _) = await BuildSpecAsync(other, otherCatalog, log, ct);
                var engine = _engines.For(other);
                await engine.DeployAsync(spec, log, ct);
                other.ComposeSha = await StoredHashAsync(engine, other, spec, ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"Warning: {other.Name} could not be connected: {ex.Message}");
            }
        }
    }

    private async Task RemoveFromMailAsync(InstalledApp app, Action<string> log, CancellationToken ct)
    {
        try
        {
            await _mail.RemoveAsync(app, _catalog.Get(app.CatalogId), log, ct);
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: the relay user of the app could not be removed from MatSMTP: {ex.Message}");
        }
    }

    private async Task RemoveFromIdentityAsync(InstalledApp app, Action<string> log, CancellationToken ct)
    {
        try
        {
            await _identity.RemoveAsync(app, _catalog.Get(app.CatalogId), log, ct);
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: the app could not be removed from MatIdentity: {ex.Message}");
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

/// <summary>
/// A hash of what a compose text <em>says</em>: comments, quoting, indentation, line endings and the order of the keys do not count, so that
/// "did anyone change this stack" is not fooled by an engine that stores the YAML in its own format. A text that is not valid YAML is
/// hashed as it is.
/// </summary>
public static class ComposeHash
{
    private static readonly YamlDotNet.Serialization.IDeserializer Reader = new YamlDotNet.Serialization.DeserializerBuilder().Build();
    private static readonly YamlDotNet.Serialization.ISerializer Writer = new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build();

    public static string Of(string compose)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(compose))));

    private static string Canonical(string compose)
    {
        try
        {
            return Writer.Serialize(Sort(Reader.Deserialize<object>(compose)));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return compose.ReplaceLineEndings("\n").TrimEnd();
        }
    }

    private static object? Sort(object? node) => node switch
    {
        IDictionary<object, object> map => map.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal).ToDictionary(kv => kv.Key.ToString()!, kv => Sort(kv.Value)),
        IList<object> list => list.Select(Sort).ToList(),
        _ => node,
    };
}
