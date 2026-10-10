using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mattiverse.Core.Apps;

/// <summary>A host port that an install would publish but that another container already holds.</summary>
public sealed record PortConflict(int Port, string Protocol, string Container);

/// <summary>Chooses the host port for an app UI and finds clashes of the ports an app publishes.</summary>
public sealed class PortPlanner
{
    private readonly MattiverseDbContext _db;
    private readonly DockerService _docker;
    private readonly MattiverseOptions _options;

    public PortPlanner(MattiverseDbContext db, DockerService docker, IOptions<MattiverseOptions> options)
    {
        _db = db;
        _docker = docker;
        _options = options.Value;
    }

    /// <summary>
    /// The preferred port of the catalog entry when it is free, otherwise the first free port of the pool.
    /// A port counts as taken when another installed app was given it or a running container publishes it.
    /// </summary>
    public async Task<int?> PlanHostPortAsync(CatalogApp app, int selfPort, CancellationToken ct = default)
    {
        if (app.Ui is null)
        {
            return null;
        }

        var taken = new HashSet<int>(await _db.InstalledApps.Where(a => a.HostPort != null).Select(a => a.HostPort!.Value).ToListAsync(ct))
        {
            selfPort,
        };
        foreach (var published in await _docker.ListPublishedPortsAsync(ct))
        {
            taken.Add(published.HostPort);
        }

        if (app.Ui.HostPort is { } preferred && !taken.Contains(preferred))
        {
            return preferred;
        }

        for (var port = _options.HostPortRangeStart; port <= _options.HostPortRangeEnd; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("No free host port is left in the configured range.");
    }

    /// <summary>The host ports the compose file publishes by itself (with the variables applied) that a running container already uses.</summary>
    public async Task<List<PortConflict>> FindConflictsAsync(CatalogApp app, IReadOnlyDictionary<string, string> variables, string ownProject, CancellationToken ct = default)
    {
        var conflicts = new List<PortConflict>();
        var published = await _docker.ListPublishedPortsAsync(ct);
        foreach (var fixedPort in app.FixedPorts)
        {
            var resolved = CatalogParser.ParsePort(EnvSubstitution.Apply(fixedPort.Raw, variables));
            if (resolved is null)
            {
                continue;
            }

            var holder = published.FirstOrDefault(p =>
                p.HostPort == resolved.HostPort &&
                p.Protocol.Equals(resolved.Protocol, StringComparison.OrdinalIgnoreCase) &&
                !p.Container.StartsWith(ownProject, StringComparison.Ordinal));
            if (holder is not null)
            {
                conflicts.Add(new PortConflict(resolved.HostPort, resolved.Protocol, holder.Container));
            }
        }

        return conflicts;
    }
}
