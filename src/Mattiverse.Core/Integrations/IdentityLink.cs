using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Core.Integrations;

/// <summary>
/// Mattiverse and MatIdentity, the single sign-on (MatDesign docs/PLATFORM.md, "Sign-in with MatIdentity"): Mattiverse's OWN sign-in through it (the client
/// <c>mattiverse</c>, <see cref="ConnectSelfAsync"/>) and the issuer of MatIdentity itself (its public address). The apps are not here: they consume
/// <c>identity.oidc</c> and the broker binds them like any capability (<see cref="BindingBroker"/>, MatIdentity is the provider).
/// </summary>
public sealed class IdentityLink
{
    private readonly MattiverseDbContext _db;
    private readonly MatIdentityClient _identity;
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly ComposeFiles _files;
    private readonly DockerService _docker;
    private readonly AppAddresses _addresses;

    public IdentityLink(MattiverseDbContext db, MatIdentityClient identity, SystemConfigService config, ProxyPublisher proxy, ComposeFiles files, DockerService docker, AppAddresses addresses)
    {
        _docker = docker;
        _addresses = addresses;
        _db = db;
        _identity = identity;
        _config = config;
        _proxy = proxy;
        _files = files;
    }

    /// <summary>MatIdentity itself, before its stack is deployed: its public address as the issuer of its tokens. Other apps are left alone.</summary>
    public async Task ApplyIssuerAsync(InstalledApp app, CatalogApp catalog, Dictionary<string, string> variables, CancellationToken ct)
    {
        if (catalog.Id == MatIdentityClient.CatalogIdValue)
        {
            variables[ManagedVariables.IdentityIssuer] = await PublicUrlAsync(app, ct) ?? string.Empty;
        }
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

    /// <summary>The path of Mattiverse's own back-channel logout (the web project's <c>BackchannelLogout</c>).</summary>
    public const string SelfBackchannelLogoutPath = "/signout-backchannel";

    /// <summary>
    /// Where MatIdentity posts the logout tokens of Mattiverse itself: its own container on the shared network (<paramref name="selfContainer"/>) and the port
    /// it listens on there. Empty (MatIdentity removes it) when Mattiverse does not run in Docker (development): MatIdentity could not reach it.
    /// </summary>
    public static string SelfBackchannelLogoutUriOf(string? selfContainer, int port)
        => string.IsNullOrEmpty(selfContainer) ? string.Empty : $"http://{selfContainer}:{port}{SelfBackchannelLogoutPath}";

    /// <summary>The address the browser opens (<see cref="AppAddresses.PublicUrlAsync"/>).</summary>
    public Task<string?> PublicUrlAsync(InstalledApp app, CancellationToken ct) => _addresses.PublicUrlAsync(app, ct);

    /// <summary>Every address the browser may use for the app (<see cref="AppAddresses.AddressesAsync"/>).</summary>
    public Task<IReadOnlyList<string>> AddressesAsync(InstalledApp app, CancellationToken ct) => _addresses.AddressesAsync(app, ct);
}
