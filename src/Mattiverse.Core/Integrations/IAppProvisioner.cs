using Mattiverse.Core.Catalog;
using Mattiverse.Core.Engines;
using Mattiverse.Core.Entities;

namespace Mattiverse.Core.Integrations;

/// <summary>
/// Sets an app up after it has started, so it works with the rest of Mattiverse without manual steps. Which provisioner runs is chosen
/// by the <c>provision</c> key of the catalog entry.
/// </summary>
public interface IAppProvisioner
{
    string Key { get; }

    Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct);
}

/// <summary>MatDock: waits for the API and makes sure MatDock manages this host's Docker engine, so the stacks of the other apps can be deployed there.</summary>
public sealed class MatDockProvisioner : IAppProvisioner
{
    private readonly MatDockClient _matdock;

    public MatDockProvisioner(MatDockClient matdock)
    {
        _matdock = matdock;
    }

    public string Key => "matdock";

    public async Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        if (!await _matdock.WaitReadyAsync(TimeSpan.FromSeconds(120), log, ct))
        {
            log("The MatDock API did not answer. This MatDock image may be older than its API: the apps are installed by Mattiverse itself until MatDock is updated.");
            return;
        }

        await _matdock.EnsureLocalEnvironmentAsync(ct);
        log("MatDock manages this host's Docker engine now; apps are deployed as MatDock stacks.");
    }
}

/// <summary>Matcad: waits for the API, then brings it in line with the settings (base domain, DNS account, a route for every app).</summary>
public sealed class MatcadProvisioner : IAppProvisioner
{
    private readonly MatcadClient _matcad;
    private readonly ProxyPublisher _proxy;

    public MatcadProvisioner(MatcadClient matcad, ProxyPublisher proxy)
    {
        _matcad = matcad;
        _proxy = proxy;
    }

    public string Key => "matcad";

    public async Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        if (!await _matcad.WaitReadyAsync(TimeSpan.FromSeconds(120), log, ct))
        {
            log("The Matcad API did not answer in time. Apps stay reachable on their ports; run the setup again later to create the routes.");
            return;
        }

        log("The Matcad API is ready.");
        await _proxy.PublishAllAsync(log, ct);
    }
}

/// <summary>Matddns: waits for the API, then writes the DNS records of the apps (when there is a domain and a DNS account).</summary>
public sealed class MatddnsProvisioner : IAppProvisioner
{
    private readonly MatddnsClient _matddns;
    private readonly DnsPublisher _dns;

    public MatddnsProvisioner(MatddnsClient matddns, DnsPublisher dns)
    {
        _matddns = matddns;
        _dns = dns;
    }

    public string Key => "matddns";

    public async Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        if (!await _matddns.WaitReadyAsync(TimeSpan.FromSeconds(120), log, ct))
        {
            log("The Matddns API did not answer. This Matddns image may be older than its API: set up its sources and records in its own UI.");
            return;
        }

        log("The Matddns API is ready.");
        await _dns.PublishAllAsync(log, null, ct);
    }
}

/// <summary>MatIdentity: waits for its API; Mattiverse then registers the apps that sign in through it (<see cref="Apps.AppService.ConnectIdentityAppsAsync"/>).</summary>
public sealed class MatIdentityProvisioner : IAppProvisioner
{
    private readonly MatIdentityClient _identity;

    public MatIdentityProvisioner(MatIdentityClient identity)
    {
        _identity = identity;
    }

    public string Key => "matidentity";

    public async Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        if (!await _identity.WaitReadyAsync(TimeSpan.FromSeconds(120), log, ct))
        {
            log("The MatIdentity API did not answer. This MatIdentity image may be older than its API: the apps keep their own sign-in until it is updated.");
            return;
        }

        log("MatIdentity is ready: the apps that can use it are connected now.");
    }
}
