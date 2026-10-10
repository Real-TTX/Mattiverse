using Mattiverse.Core.Catalog;
using Mattiverse.Core.Entities;

namespace Mattiverse.Core.Integrations;

/// <summary>
/// Sets an app up after it has started, so it works with the rest of the Mattiverse without manual steps
/// (for example: give the reverse proxy its base domain and create the routes of all apps). Which provisioner runs is chosen
/// by the <c>provision</c> key of the catalog entry.
/// </summary>
public interface IAppProvisioner
{
    string Key { get; }

    Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct);
}

/// <summary>Waits for the Matcad API, hands it the base domain and publishes Mattiverse and all installed apps.</summary>
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
        if (!await _matcad.WaitReadyAsync(TimeSpan.FromSeconds(90), log, ct))
        {
            log("The Matcad API did not answer in time. Apps stay reachable on their ports; run the setup again later to create the routes.");
            return;
        }

        log("The Matcad API is ready.");
        await _proxy.PublishAllAsync(log, ct);
    }
}
