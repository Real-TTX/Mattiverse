using System.Text.RegularExpressions;
using Mattiverse.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mattiverse.Core.Docker;

/// <summary>
/// Typed Docker operations (containers, networks, volumes, ports) on top of <see cref="DockerCli"/>.
/// Mattiverse finds "its" containers by the labels it sets (<c>mattiverse.managed</c>, <c>mattiverse.instance</c>)
/// and by the compose project label, so Docker itself stays the source of truth for what runs.
/// </summary>
public sealed partial class DockerService
{
    public const string ManagedLabel = "mattiverse.managed";
    public const string InstanceLabel = "mattiverse.instance";
    public const string ServiceLabel = "mattiverse.service";
    public const string AppLabel = "mattiverse.app";
    private const string ComposeProjectLabel = "com.docker.compose.project";

    private readonly DockerCli _cli;
    private readonly MattiverseOptions _options;
    private readonly ILogger<DockerService> _logger;
    private string? _selfName;
    private bool _selfResolved;

    public DockerService(DockerCli cli, IOptions<MattiverseOptions> options, ILogger<DockerService> logger)
    {
        _cli = cli;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------ engine

    public async Task<DockerEngineInfo> GetEngineInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _cli.RunAsync(
                ["info", "--format", "{{.ServerVersion}}|{{.OperatingSystem}}|{{.NCPU}}|{{.MemTotal}}|{{.ContainersRunning}}|{{.Containers}}|{{.Images}}"],
                ct: ct);
            if (!result.Success)
            {
                return Unavailable(result.Message);
            }

            var parts = result.StdOut.Trim().Split('|');
            if (parts.Length < 7)
            {
                return Unavailable("Unexpected output of docker info.");
            }

            return new DockerEngineInfo(
                true,
                null,
                parts[0],
                parts[1],
                ToInt(parts[2]),
                ToLong(parts[3]),
                ToInt(parts[4]),
                ToInt(parts[5]),
                ToInt(parts[6]));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Unavailable("The docker CLI is not available: " + ex.Message);
        }
    }

    private static DockerEngineInfo Unavailable(string error)
        => new(false, error, string.Empty, string.Empty, 0, 0, 0, 0, 0);

    // ------------------------------------------------------------------ network

    /// <summary>Creates the shared network if needed and connects Mattiverse itself to it, so it can reach the apps by container name.</summary>
    public async Task EnsureNetworkAsync(CancellationToken ct = default)
    {
        var name = _options.NetworkName;
        var inspect = await _cli.RunAsync(["network", "inspect", name, "--format", "{{.Id}}"], ct: ct);
        if (!inspect.Success)
        {
            var create = await _cli.RunAsync(["network", "create", "--label", $"{ManagedLabel}=true", name], ct: ct);
            if (!create.Success && !create.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Could not create the Docker network '{name}': {create.Message}");
            }
        }

        var self = await GetSelfContainerNameAsync(ct);
        if (self is null)
        {
            return;
        }

        var connect = await _cli.RunAsync(["network", "connect", name, self], ct: ct);
        if (!connect.Success && !connect.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Could not connect Mattiverse to the network '{Network}': {Message}", name, connect.Message);
        }
    }

    /// <summary>The name of the container Mattiverse runs in, or <c>null</c> when it does not run in Docker (local development).</summary>
    public async Task<string?> GetSelfContainerNameAsync(CancellationToken ct = default)
    {
        if (_selfResolved)
        {
            return _selfName;
        }

        _selfResolved = true;
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(hostname) || !File.Exists("/.dockerenv"))
        {
            return null;
        }

        var inspect = await _cli.RunAsync(["inspect", "--format", "{{.Name}}", hostname], ct: ct);
        if (inspect.Success)
        {
            _selfName = inspect.StdOut.Trim().TrimStart('/');
        }

        return _selfName;
    }

    // ------------------------------------------------------------------ containers

    /// <summary>All containers that carry the Mattiverse label, optionally only those of one instance.</summary>
    public async Task<List<ContainerInfo>> ListContainersAsync(string? instance = null, CancellationToken ct = default)
    {
        var args = new List<string> { "ps", "-a", "--no-trunc", "--filter", $"label={ManagedLabel}=true" };
        if (instance is not null)
        {
            args.AddRange(["--filter", $"label={InstanceLabel}={instance}"]);
        }

        args.AddRange(["--format", $"{{{{.ID}}}}\t{{{{.Names}}}}\t{{{{.Image}}}}\t{{{{.State}}}}\t{{{{.Status}}}}\t{{{{.Label \"{InstanceLabel}\"}}}}\t{{{{.Label \"{ServiceLabel}\"}}}}\t{{{{.Ports}}}}"]);
        var result = await _cli.RunAsync(args, ct: ct);
        if (!result.Success)
        {
            _logger.LogWarning("docker ps failed: {Message}", result.Message);
            return [];
        }

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(p => p.Length >= 8)
            .Select(p => new ContainerInfo(p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]))
            .ToList();
    }

    /// <summary>Summarizes the containers of one app as a single state.</summary>
    public static RuntimeInfo Summarize(IReadOnlyCollection<ContainerInfo> containers)
    {
        if (containers.Count == 0)
        {
            return new RuntimeInfo(RuntimeState.Missing, 0, 0, "Not created");
        }

        var running = containers.Count(c => c.IsRunning);
        var broken = containers.Any(c =>
            c.State.Equals("restarting", StringComparison.OrdinalIgnoreCase) ||
            c.State.Equals("dead", StringComparison.OrdinalIgnoreCase) ||
            c.Status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase) ||
            (c.State.Equals("exited", StringComparison.OrdinalIgnoreCase) && !c.Status.Contains("(0)", StringComparison.Ordinal)));

        if (broken)
        {
            return new RuntimeInfo(RuntimeState.Unhealthy, running, containers.Count, "Needs attention");
        }

        if (running == containers.Count)
        {
            return new RuntimeInfo(RuntimeState.Running, running, containers.Count, "Running");
        }

        return running == 0
            ? new RuntimeInfo(RuntimeState.Stopped, 0, containers.Count, "Stopped")
            : new RuntimeInfo(RuntimeState.Partial, running, containers.Count, $"{running} of {containers.Count} running");
    }

    /// <summary>The last lines of a container's log.</summary>
    public async Task<string> GetLogsAsync(string container, int tail = 300, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["logs", "--tail", tail.ToString(), "--timestamps", container], ct: ct);

        // docker logs writes the container's stderr to its own stderr: both are log content.
        return result.Success ? (result.StdOut + result.StdErr).TrimEnd() : result.Message;
    }

    /// <summary>The ports that the containers of a project publish are read from the labelled containers; this lists every published host port of the engine.</summary>
    public async Task<List<PublishedPort>> ListPublishedPortsAsync(CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["ps", "--format", "{{.Names}}\t{{.Ports}}"], ct: ct);
        var ports = new List<PublishedPort>();
        if (!result.Success)
        {
            return ports;
        }

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            foreach (Match match in PublishedPortRegex().Matches(parts[1]))
            {
                var first = int.Parse(match.Groups["from"].Value);
                var last = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value) : first;
                var protocol = match.Groups["proto"].Value;
                for (var port = first; port <= last && port - first < 1000; port++)
                {
                    ports.Add(new PublishedPort(port, protocol, parts[0]));
                }
            }
        }

        return ports;
    }

    // ------------------------------------------------------------------ volumes

    /// <summary>The named volumes that compose created for a project (without external volumes).</summary>
    public async Task<List<string>> ListProjectVolumesAsync(string project, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(
            ["volume", "ls", "--filter", $"label={ComposeProjectLabel}={project}", "--format", "{{.Name}}"],
            ct: ct);
        return result.Success
            ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];
    }

    public async Task<bool> VolumeExistsAsync(string name, CancellationToken ct = default)
        => (await _cli.RunAsync(["volume", "inspect", name], ct: ct)).Success;

    /// <summary>Creates a volume with the labels compose expects, so <c>docker compose up</c> adopts it without a warning.</summary>
    public async Task CreateProjectVolumeAsync(string project, string logicalName, CancellationToken ct = default)
    {
        var fullName = $"{project}_{logicalName}";
        var result = await _cli.RunAsync(
            ["volume", "create", "--label", $"{ComposeProjectLabel}={project}", "--label", $"com.docker.compose.volume={logicalName}", fullName],
            ct: ct);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not create the volume '{fullName}': {result.Message}");
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>The image id (sha256:...) of a locally available image reference, or <c>null</c>.</summary>
    public async Task<string?> GetImageIdAsync(string imageReference, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["image", "inspect", "--format", "{{.Id}}", imageReference], ct: ct);
        return result.Success ? result.StdOut.Trim() : null;
    }

    /// <summary>The repo digest (<c>repo@sha256:...</c>) of a container's image, if it came from a registry.</summary>
    public async Task<string?> GetContainerImageDigestAsync(string container, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["inspect", "--format", "{{.Image}}", container], ct: ct);
        if (!result.Success)
        {
            return null;
        }

        var image = await _cli.RunAsync(["image", "inspect", "--format", "{{if .RepoDigests}}{{index .RepoDigests 0}}{{end}}", result.StdOut.Trim()], ct: ct);
        var digest = image.Success ? image.StdOut.Trim() : null;
        return string.IsNullOrEmpty(digest) ? null : digest;
    }

    // ------------------------------------------------------------------ helpers

    private static int ToInt(string value) => int.TryParse(value, out var n) ? n : 0;

    private static long ToLong(string value) => long.TryParse(value, out var n) ? n : 0;

    // 0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, 0.0.0.0:21000-21049->21000-21049/tcp
    [GeneratedRegex(@":(?<from>\d+)(?:-(?<to>\d+))?->\d+(?:-\d+)?/(?<proto>tcp|udp)")]
    private static partial Regex PublishedPortRegex();
}
