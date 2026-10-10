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
/// Gives apps a domain: with Matcad and a base domain every app gets <c>&lt;slug&gt;.&lt;base domain&gt;</c> and so does Mattiverse itself. With a
/// DNS account, Matcad also gets it as a DNS provider, so the certificates are issued through the DNS challenge (no open port 80 needed).
/// Without Matcad or a base domain nothing happens and the apps stay reachable on their host port.
/// </summary>
public sealed class ProxyPublisher
{
    public const string ProviderName = "Mattiverse DNS";

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

    /// <summary>
    /// Brings Matcad in line with the settings: base domain, the DNS account for the certificates (with the wildcard route that uses it), and a
    /// route for Mattiverse and every installed app. Routes that Mattiverse made earlier and no longer wants (an old domain, a removed DNS
    /// account) are removed. Used after the proxy was installed or the domain or the DNS account changed.
    /// </summary>
    public async Task PublishAllAsync(Action<string> log, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(ct))
        {
            log("No reverse proxy with a base domain: nothing to publish.");
            return;
        }

        var config = await _config.GetAsync(ct);
        var baseDomain = config.BaseDomain!.Trim().Trim('.').ToLowerInvariant();
        await _matcad.ConfigureAsync(baseDomain, ct);
        var providerId = await EnsureProviderAsync(config, log, ct);

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (providerId is not null && await PublishWildcardAsync(baseDomain, providerId.Value, log, ct))
        {
            wanted.Add("*." + baseDomain);
        }

        if (await PublishSelfAsync(baseDomain, log, ct))
        {
            wanted.Add(HostFor("mattiverse", baseDomain));
        }

        var apps = await _db.InstalledApps.AsNoTracking()
            .Where(a => a.Lifecycle == AppLifecycle.Installed && a.UiService != null && a.Role != AppRoles.Proxy)
            .ToListAsync(ct);
        foreach (var app in apps)
        {
            wanted.Add(HostFor(app.Slug, baseDomain));
            await PublishAsync(app, log, ct);
        }

        try
        {
            foreach (var host in await _matcad.RemoveManagedExceptAsync(wanted, ct))
            {
                log($"Removed the route {host}, which Mattiverse does not need any more.");
            }

            // Matcad refuses to drop a provider that a route still uses: the wildcard route went first.
            if (providerId is null && await _matcad.FindProviderAsync(ProviderName, ct) is not null)
            {
                await _matcad.DeleteProviderAsync(ProviderName, ct);
                log("Removed the DNS account from Matcad.");
            }
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not clean up in Matcad: {ex.Message}");
        }
    }

    /// <summary>
    /// Gives Matcad the DNS account (for certificates through the DNS challenge). Returns the id of the provider, or <c>null</c> when there is
    /// no (complete) DNS account.
    /// </summary>
    private async Task<long?> EnsureProviderAsync(SystemConfig config, Action<string> log, CancellationToken ct)
    {
        try
        {
            if (DnsProviders.Find(config.Dns.Provider) is not { } provider)
            {
                return null;
            }

            var credentials = await _config.GetDnsCredentialsAsync(ct);
            if (provider.Fields.Any(f => string.IsNullOrEmpty(credentials.GetValueOrDefault(f.Key))))
            {
                log("The DNS account is incomplete: Matcad uses the HTTP challenge for the certificates.");
                return null;
            }

            var id = await _matcad.UpsertProviderAsync(ProviderName, provider.MatcadType, DnsProviders.ToMatcad(provider, credentials), ct);
            log($"Matcad uses your {provider.Name} account for the certificates.");
            return id;
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not give Matcad the DNS account: {ex.Message}");
            return null;
        }
    }

    /// <summary>The wildcard route that makes Matcad issue one certificate for <c>*.&lt;domain&gt;</c> through the DNS challenge.</summary>
    private async Task<bool> PublishWildcardAsync(string baseDomain, long providerId, Action<string> log, CancellationToken ct)
    {
        try
        {
            var target = UrlFor(HostFor("mattiverse", baseDomain));
            await _matcad.UpsertWildcardRouteAsync(baseDomain, providerId, target, "Mattiverse wildcard", ct);
            log($"One wildcard certificate for *.{baseDomain} (DNS challenge); other names on it lead to {target}.");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not create the wildcard route: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> PublishSelfAsync(string baseDomain, Action<string> log, CancellationToken ct)
    {
        var self = await _docker.GetSelfContainerNameAsync(ct);
        if (self is null)
        {
            return false;
        }

        var host = HostFor("mattiverse", baseDomain);
        try
        {
            await _matcad.UpsertRouteAsync(host, $"http://{self}:{SelfPort()}", "Mattiverse", ct);
            log($"Published Mattiverse at {UrlFor(host)}");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not publish Mattiverse: {ex.Message}");
            return false;
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
