using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mattiverse.Core.Integrations;

/// <summary>
/// Gives apps a domain: with a reverse proxy (Matcad) and a base domain every app gets <c>&lt;slug&gt;.&lt;base domain&gt;</c> and so
/// does Mattiverse itself. Without a proxy or a base domain nothing happens and the apps stay reachable on their host port.
/// </summary>
public sealed class ProxyPublisher
{
    private readonly MattiverseDbContext _db;
    private readonly SystemConfigService _config;
    private readonly MatcadClient _matcad;
    private readonly DockerService _docker;
    private readonly ComposeFiles _files;
    private readonly ILogger<ProxyPublisher> _logger;

    public ProxyPublisher(
        MattiverseDbContext db,
        SystemConfigService config,
        MatcadClient matcad,
        DockerService docker,
        ComposeFiles files,
        ILogger<ProxyPublisher> logger)
    {
        _db = db;
        _config = config;
        _matcad = matcad;
        _docker = docker;
        _files = files;
        _logger = logger;
    }

    /// <summary>The host name of an app below the base domain.</summary>
    public static string HostFor(string slug, string baseDomain) => $"{slug}.{baseDomain.Trim().Trim('.')}";

    /// <summary>The public URL of a host; plain http for development domains (<c>*.localhost</c>) that cannot get a certificate.</summary>
    public static string UrlFor(string host)
        => host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ? $"http://{host}" : $"https://{host}";

    /// <summary>Is there a proxy Mattiverse can create routes on, and a base domain to use?</summary>
    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
    {
        var config = await _config.GetAsync(ct);
        return !string.IsNullOrWhiteSpace(config.BaseDomain) && await _matcad.IsAvailableAsync(ct);
    }

    /// <summary>Creates (or updates) the route of one app and stores its public URL. Returns the URL, or <c>null</c> when nothing was published.</summary>
    public async Task<string?> PublishAsync(InstalledApp app, Action<string> log, CancellationToken ct = default)
    {
        if (app.UiService is null || app.UiPort is null || !await IsActiveAsync(ct))
        {
            return null;
        }

        var config = await _config.GetAsync(ct);
        var host = HostFor(app.Slug, config.BaseDomain!);
        var upstream = $"http://{_files.UiHost(app)}:{app.UiPort}";
        try
        {
            await _matcad.UpsertRouteAsync(host, upstream, app.Name, ct);
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not publish {app.Name} on {host}: {ex.Message}");
            _logger.LogWarning(ex, "Publishing {Slug} failed.", app.Slug);
            return null;
        }

        var url = UrlFor(host);
        log($"Published {app.Name} at {url}");
        var tracked = await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == app.Id, ct);
        if (tracked is not null)
        {
            tracked.PublicUrl = url;
            await _db.SaveChangesAsync(ct);
        }

        app.PublicUrl = url;
        return url;
    }

    public async Task UnpublishAsync(InstalledApp app, Action<string> log, CancellationToken ct = default)
    {
        var config = await _config.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(config.BaseDomain) || string.IsNullOrEmpty(app.PublicUrl) || !await _matcad.IsAvailableAsync(ct))
        {
            return;
        }

        try
        {
            await _matcad.DeleteRouteAsync(HostFor(app.Slug, config.BaseDomain), ct);
            log($"Removed the route of {app.Name}.");
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not remove the route of {app.Name}: {ex.Message}");
        }
    }

    /// <summary>Publishes Mattiverse itself and every installed app. Used after the proxy was installed or the base domain changed.</summary>
    public async Task PublishAllAsync(Action<string> log, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(ct))
        {
            log("No reverse proxy with a base domain: nothing to publish.");
            return;
        }

        var config = await _config.GetAsync(ct);
        await _matcad.SetBaseDomainAsync(config.BaseDomain, ct);

        await PublishSelfAsync(config, log, ct);
        var apps = await _db.InstalledApps.AsNoTracking()
            .Where(a => a.Lifecycle == AppLifecycle.Installed && a.UiService != null && a.Role != AppRoles.Proxy)
            .ToListAsync(ct);
        foreach (var app in apps)
        {
            await PublishAsync(app, log, ct);
        }
    }

    private async Task PublishSelfAsync(SystemConfig config, Action<string> log, CancellationToken ct)
    {
        var self = await _docker.GetSelfContainerNameAsync(ct);
        if (self is null)
        {
            return;
        }

        var host = HostFor("mattiverse", config.BaseDomain!);
        try
        {
            await _matcad.UpsertRouteAsync(host, $"http://{self}:{SelfPort()}", "Mattiverse", ct);
            log($"Published Mattiverse at {UrlFor(host)}");
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not publish Mattiverse: {ex.Message}");
        }
    }

    /// <summary>The port Mattiverse listens on inside its container (from <c>ASPNETCORE_URLS</c>, default 8030).</summary>
    public static int SelfPort()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var match = System.Text.RegularExpressions.Regex.Match(urls ?? string.Empty, @":(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 8030;
    }
}
