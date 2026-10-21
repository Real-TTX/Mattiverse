using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Core.Integrations;

/// <summary>
/// Connects the apps to MatIdentity, the single sign-on (MatDesign docs/PLATFORM.md, "Sign-in with MatIdentity"). Mattiverse does all of it, nobody types
/// a client id: when an app that can sign people in through MatIdentity (<see cref="CatalogApp.Identity"/>) is installed or updated and MatIdentity is
/// installed, the app is registered there as a client (its addresses, <c>managedBy: mattiverse</c>) and its <c>MATIDENTITY_*</c> variables are filled; without
/// MatIdentity they are empty and the app keeps its own sign-in. MatIdentity itself gets its public address as the issuer of its tokens.
/// </summary>
public sealed class IdentityLink
{
    private readonly MattiverseDbContext _db;
    private readonly MatIdentityClient _identity;
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly ComposeFiles _files;
    private readonly DockerService _docker;

    public IdentityLink(MattiverseDbContext db, MatIdentityClient identity, SystemConfigService config, ProxyPublisher proxy, ComposeFiles files, DockerService docker)
    {
        _docker = docker;
        _db = db;
        _identity = identity;
        _config = config;
        _proxy = proxy;
        _files = files;
    }

    /// <summary>
    /// Fills the variables Mattiverse manages for this app, before its stack is deployed: the issuer of MatIdentity itself, or the client of an app that
    /// signs in through it. <paramref name="variables"/> is changed in place.
    /// </summary>
    public async Task ApplyAsync(InstalledApp app, CatalogApp catalog, Dictionary<string, string> variables, Action<string> log, CancellationToken ct)
    {
        if (catalog.Id == MatIdentityClient.CatalogIdValue)
        {
            variables[ManagedVariables.IdentityIssuer] = await PublicUrlAsync(app, ct) ?? string.Empty;
            return;
        }

        if (catalog.Identity is null)
        {
            return;
        }

        var identityApp = await _db.InstalledApps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CatalogId == MatIdentityClient.CatalogIdValue && a.Lifecycle == AppLifecycle.Installed, ct);
        if (identityApp is null || await _identity.GetStatusAsync(ct) is not { ApiVersion: >= 1 })
        {
            if (!string.IsNullOrEmpty(variables.GetValueOrDefault(ManagedVariables.IdentityAuthority)))
            {
                log("MatIdentity is not there (any more): the app uses its own sign-in again.");
            }

            Clear(variables);
            return;
        }

        var bases = await AddressesAsync(app, ct);
        var clientId = ClientIdOf(app);
        var hasSecret = !string.IsNullOrEmpty(variables.GetValueOrDefault(ManagedVariables.IdentityClientSecret))
                        && variables.GetValueOrDefault(ManagedVariables.IdentityClientId) == clientId;
        var registration = await _identity.UpsertClientAsync(
            clientId,
            app.Name,
            bases.FirstOrDefault(),
            bases.Select(b => b + catalog.Identity.Callback).ToList(),
            bases.Select(b => b + catalog.Identity.SignedOut).ToList(),
            callsAllApps: false,
            renewSecret: !hasSecret,
            // MatIdentity tells the app when a person signed out there, server to server: the app's address on the shared network.
            BackchannelLogoutUriOf(app.UiService is null ? null : _files.UiHost(app), app.UiPort, catalog.Identity),
            ct);

        variables[ManagedVariables.IdentityAuthority] = await PublicUrlAsync(identityApp, ct) ?? string.Empty;
        // The app reaches MatIdentity on the shared network; the browser at its public address (the app rewrites its server-to-server calls).
        variables[ManagedVariables.IdentityInternalUrl] = $"http://{_files.UiHost(identityApp)}:{identityApp.UiPort}";
        variables[ManagedVariables.IdentityClientId] = clientId;
        if (registration.Secret is not null)
        {
            variables[ManagedVariables.IdentityClientSecret] = registration.Secret;
        }

        log($"Signs in through MatIdentity ({variables[ManagedVariables.IdentityAuthority]}) as the app \"{clientId}\".");
    }

    /// <summary>The client id of Mattiverse itself in MatIdentity.</summary>
    public const string SelfClientId = "mattiverse";

    /// <summary>
    /// Registers Mattiverse itself in MatIdentity, so people sign in to Mattiverse through it too, and keeps how in the settings. <paramref name="origin"/> is the
    /// address a person reaches Mattiverse at now (a new one is added to the redirect addresses); the route of the proxy is added on its own.
    /// Without MatIdentity the sign-in through it is switched off. Returns whether Mattiverse signs in through MatIdentity now.
    /// </summary>
    public async Task<bool> ConnectSelfAsync(string? origin, Action<string> log, CancellationToken ct)
    {
        var identityApp = await _db.InstalledApps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CatalogId == MatIdentityClient.CatalogIdValue && a.Lifecycle == AppLifecycle.Installed, ct);
        var endpoint = await _identity.FindAsync(ct);
        if (identityApp is null || endpoint is null || await _identity.GetStatusAsync(ct) is not { ApiVersion: >= 1 })
        {
            if ((await _config.GetAsync(ct)).Identity is not null)
            {
                await _config.SetIdentityAsync(null, null, ct);
                log("Mattiverse signs people in itself again.");
            }

            return false;
        }

        var config = await _config.GetAsync(ct);
        var origins = new List<string>(config.Identity?.Origins ?? []);
        if (!string.IsNullOrWhiteSpace(config.BaseDomain) && await _proxy.IsActiveAsync(ct))
        {
            origins.Add(ProxyPublisher.UrlFor(ProxyPublisher.HostFor("mattiverse", config.BaseDomain)));
        }

        if (!string.IsNullOrWhiteSpace(origin))
        {
            origins.Add(origin.TrimEnd('/'));
        }

        origins = origins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var secret = await _config.GetIdentitySecretAsync(ct);
        var registration = await _identity.UpsertClientAsync(
            SelfClientId,
            "Mattiverse",
            origins.FirstOrDefault(),
            origins.Select(o => o + "/signin-oidc").ToList(),
            origins.Select(o => o + "/signout-callback-oidc").ToList(),
            callsAllApps: true,
            renewSecret: string.IsNullOrEmpty(secret),
            // MatIdentity ends Mattiverse's sessions of a person who signed out there: Mattiverse's container on the shared network (none outside Docker).
            SelfBackchannelLogoutUriOf(await _docker.GetSelfContainerNameAsync(ct), ProxyPublisher.SelfPort()),
            ct);

        await _config.SetIdentityAsync(new IdentitySignIn
        {
            Authority = await PublicUrlAsync(identityApp, ct) ?? endpoint.BaseUrl,
            // Mattiverse reaches MatIdentity like it reaches the engines (the shared network, or the host port during development).
            InternalUrl = endpoint.BaseUrl,
            ClientId = SelfClientId,
            Origins = origins,
        }, registration.Secret, ct);
        log("Mattiverse signs people in through MatIdentity.");
        return true;
    }

    /// <summary>Removes the client of an app that is being uninstalled (only what Mattiverse registered).</summary>
    public async Task RemoveAsync(InstalledApp app, CatalogApp? catalog, Action<string> log, CancellationToken ct)
    {
        if (catalog?.Identity is null || await _identity.GetStatusAsync(ct) is not { ApiVersion: >= 1 })
        {
            return;
        }

        await _identity.DeleteClientAsync(ClientIdOf(app), ct);
        log("Removed from MatIdentity.");
    }

    /// <summary>The path of Mattiverse's own back-channel logout (the web project's <c>BackchannelLogout</c>).</summary>
    public const string SelfBackchannelLogoutPath = "/signout-backchannel";

    /// <summary>
    /// Where MatIdentity posts the logout tokens of an app (OpenID Connect Back-Channel Logout): the container of its web service on the shared network and
    /// the port inside it, with the path of the catalog entry (<c>http://mattiverse-matsplit-web:8080/signout-backchannel</c>). MatIdentity calls it server to
    /// server, so it is never the public address. Empty (MatIdentity removes it) when the app has no web service or port.
    /// </summary>
    public static string BackchannelLogoutUriOf(string? uiHost, int? uiPort, CatalogIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return string.IsNullOrEmpty(uiHost) || uiPort is not { } port || string.IsNullOrWhiteSpace(identity.Backchannel)
            ? string.Empty
            : $"http://{uiHost}:{port}/{identity.Backchannel.Trim().TrimStart('/')}";
    }

    /// <summary>
    /// Where MatIdentity posts the logout tokens of Mattiverse itself: its own container on the shared network (<paramref name="selfContainer"/>) and the port
    /// it listens on there. Empty (MatIdentity removes it) when Mattiverse does not run in Docker (development): MatIdentity could not reach it.
    /// </summary>
    public static string SelfBackchannelLogoutUriOf(string? selfContainer, int port)
        => string.IsNullOrEmpty(selfContainer) ? string.Empty : $"http://{selfContainer}:{port}{SelfBackchannelLogoutPath}";

    /// <summary>The client id of an app in MatIdentity: its slug (the id of the app in this Mattiverse; at least two characters).</summary>
    public static string ClientIdOf(InstalledApp app) => app.Slug.Length >= 2 ? app.Slug : app.Slug + "-app";

    /// <summary>The address the browser opens: the route of the reverse proxy when there is one, else this host and the app's port.</summary>
    public async Task<string?> PublicUrlAsync(InstalledApp app, CancellationToken ct)
        => (await AddressesAsync(app, ct)).FirstOrDefault();

    /// <summary>Every address the browser may use for the app: the route (when a proxy and a domain are set up) and this host with the app's port.</summary>
    public async Task<IReadOnlyList<string>> AddressesAsync(InstalledApp app, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.BaseDomain) && await _proxy.IsActiveAsync(ct))
        {
            result.Add(ProxyPublisher.UrlFor(ProxyPublisher.HostFor(app.Slug, config.BaseDomain)));
        }

        if (app.HostPort is { } port)
        {
            var host = string.IsNullOrWhiteSpace(config.PublicHost) ? "localhost" : config.PublicHost.Trim();
            result.Add($"http://{(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host)}:{port}");
        }

        return result;
    }

    private static void Clear(Dictionary<string, string> variables)
    {
        variables[ManagedVariables.IdentityAuthority] = string.Empty;
        variables[ManagedVariables.IdentityInternalUrl] = string.Empty;
        variables[ManagedVariables.IdentityClientId] = string.Empty;
        variables[ManagedVariables.IdentityClientSecret] = string.Empty;
    }
}
