using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Docker;

namespace Mattiverse.Core.Integrations;

/// <summary>
/// The addresses of Mattiverse that may show an app in a frame (<c>MAT_FRAME_ANCESTORS</c>, MatDesign docs/PLATFORM.md): Mattiverse opens apps in the window of its
/// home screen, and the engines refuse a frame unless their <c>frame-ancestors</c> name the page around it. Filled before the stack is deployed for every app whose
/// compose file names <c>${MAT_FRAME_ANCESTORS}</c>; an app learns a new address of Mattiverse with its next update.
/// </summary>
public sealed class FrameLink
{
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly DockerService _docker;

    public FrameLink(SystemConfigService config, ProxyPublisher proxy, DockerService docker)
    {
        _config = config;
        _proxy = proxy;
        _docker = docker;
    }

    /// <summary>Writes the addresses into <paramref name="variables"/> for an app whose compose file names <c>MAT_FRAME_ANCESTORS</c>; other apps are left alone.</summary>
    public async Task ApplyAsync(CatalogApp catalog, Dictionary<string, string> variables, CancellationToken ct)
    {
        if (catalog.Variables.All(v => v.Key != ManagedVariables.FrameAncestors))
        {
            return;
        }

        variables[ManagedVariables.FrameAncestors] = string.Join(' ', await OriginsAsync(ct));
    }

    /// <summary>The addresses of Mattiverse now: its route behind the proxy, this machine with the port Mattiverse is published on, the addresses people signed in from.</summary>
    public async Task<IReadOnlyList<string>> OriginsAsync(CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var port = await _docker.GetSelfHostPortAsync(ProxyPublisher.SelfPort(), ct) ?? ProxyPublisher.SelfPort();
        var proxied = !string.IsNullOrWhiteSpace(config.BaseDomain) && await _proxy.IsActiveAsync(ct);
        return Origins(config, port, proxied);
    }

    /// <summary>
    /// The addresses for a configuration: <c>https://mattiverse.&lt;domain&gt;</c> when the proxy publishes it, <c>http://localhost:&lt;port&gt;</c>, the configured
    /// address of this machine with that port, and the addresses Mattiverse was signed in from (<c>Identity.Origins</c>); each as an origin, without repeats.
    /// </summary>
    public static IReadOnlyList<string> Origins(SystemConfig config, int port, bool proxied)
    {
        var candidates = new List<string>();
        if (proxied && !string.IsNullOrWhiteSpace(config.BaseDomain))
        {
            candidates.Add(ProxyPublisher.UrlFor(ProxyPublisher.HostFor("mattiverse", config.BaseDomain)));
        }

        candidates.Add($"http://localhost:{port}");
        var host = config.PublicHost?.Trim();
        if (!string.IsNullOrEmpty(host))
        {
            candidates.Add($"http://{(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host)}:{port}");
        }

        candidates.AddRange(config.Identity?.Origins ?? []);

        var origins = new List<string>();
        foreach (var candidate in candidates)
        {
            if (OriginOf(candidate) is { } origin && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                origins.Add(origin);
            }
        }

        return origins;
    }

    /// <summary><c>scheme://host[:port]</c> of an http(s) address (lower case, no default port), or <c>null</c>.</summary>
    public static string? OriginOf(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? (uri.IsDefaultPort ? $"{uri.Scheme}://{HostOf(uri)}" : $"{uri.Scheme}://{HostOf(uri)}:{uri.Port}")
            : null;

    /// <summary>The host of an origin: an IPv6 address keeps its brackets.</summary>
    private static string HostOf(Uri uri) => uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
}
