using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Mattiverse.Core.Apps;

/// <summary>
/// Turns a catalog entry into the compose file that is actually deployed: the entry's own services plus what only Mattiverse
/// knows (container names, labels, the shared network, the published port of the UI) and an <c>x-matdock</c> block so that
/// MatDock's launchpad shows the stack as an app. It is one self-contained file, because the engines (MatDock) keep exactly one
/// compose text per stack, and it is also the file that <c>docker compose</c> runs when Mattiverse deploys it itself.
/// </summary>
public sealed class ComposeRenderer
{
    private readonly MattiverseOptions _options;

    public ComposeRenderer(IOptions<MattiverseOptions> options)
    {
        _options = options.Value;
    }

    public string ProjectName(string slug) => $"{_options.ProjectPrefix}-{slug}";

    public string ContainerName(string slug, string service) => $"{_options.ProjectPrefix}-{slug}-{service}";

    /// <summary>Name by which other containers on the shared network reach the UI of an app.</summary>
    public string UiHost(InstalledApp app) => ContainerName(app.Slug, app.UiService ?? string.Empty);

    public string Render(CatalogApp catalog, InstalledApp app)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(catalog.ComposeYaml))
        {
            stream.Load(reader);
        }

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var services = (YamlMappingNode)root.Children[new YamlScalarNode("services")];

        foreach (var (keyNode, valueNode) in services.Children)
        {
            var service = (YamlMappingNode)valueNode;
            var name = ((YamlScalarNode)keyNode).Value!;
            var isUi = catalog.Ui?.Service == name;
            var shared = isUi || catalog.Shared.Contains(name);

            SetScalar(service, "container_name", ContainerName(app.Slug, name), ScalarStyle.Plain);
            MergeLabels(service, new Dictionary<string, string>
            {
                [DockerService.ManagedLabel] = "true",
                [DockerService.AppLabel] = catalog.Id,
                [DockerService.InstanceLabel] = app.Slug,
                [DockerService.ServiceLabel] = name,
            });

            if (shared)
            {
                MergeNetworks(service, ["default", _options.NetworkName]);
            }

            if (isUi && app.HostPort is { } hostPort && catalog.Ui is { } ui)
            {
                AppendToSequence(service, "ports", $"{hostPort}:{ui.Port}");
            }
        }

        AddSharedNetwork(root);
        SetMapping(root, "x-matdock", new Dictionary<string, string>
        {
            ["name"] = app.Name,
            ["description"] = catalog.Tagline,
            ["category"] = catalog.Category,
        });

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        // Compose reads the file on Linux: always LF. The YAML writer ends a document with "..."; drop it.
        var text = writer.ToString().ReplaceLineEndings("\n").TrimEnd();
        if (text.EndsWith("\n...", StringComparison.Ordinal))
        {
            text = text[..^4].TrimEnd();
        }

        return text + "\n";
    }

    // ------------------------------------------------------------------ YAML helpers

    private static void SetScalar(YamlMappingNode map, string key, string value, ScalarStyle style)
        => map.Children[new YamlScalarNode(key)] = new YamlScalarNode(value) { Style = style };

    private static void MergeLabels(YamlMappingNode service, IReadOnlyDictionary<string, string> labels)
    {
        var key = new YamlScalarNode("labels");
        if (service.Children.TryGetValue(key, out var existing) && existing is YamlSequenceNode sequence)
        {
            // The list form ("key=value") stays a list.
            foreach (var (name, value) in labels)
            {
                sequence.Add(new YamlScalarNode($"{name}={value}") { Style = ScalarStyle.DoubleQuoted });
            }

            return;
        }

        var map = existing as YamlMappingNode ?? new YamlMappingNode();
        foreach (var (name, value) in labels)
        {
            map.Children[new YamlScalarNode(name)] = new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
        }

        service.Children[key] = map;
    }

    private static void MergeNetworks(YamlMappingNode service, IReadOnlyList<string> networks)
    {
        var key = new YamlScalarNode("networks");
        if (service.Children.TryGetValue(key, out var existing) && existing is YamlMappingNode map)
        {
            foreach (var network in networks.Where(n => !map.Children.ContainsKey(new YamlScalarNode(n))))
            {
                map.Children[new YamlScalarNode(network)] = new YamlMappingNode();
            }

            return;
        }

        var sequence = existing as YamlSequenceNode ?? new YamlSequenceNode();
        var present = sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value).ToHashSet();
        foreach (var network in networks.Where(n => !present.Contains(n)))
        {
            sequence.Add(new YamlScalarNode(network));
        }

        service.Children[key] = sequence;
    }

    private static void AppendToSequence(YamlMappingNode service, string key, string value)
    {
        var node = new YamlScalarNode(key);
        var sequence = service.Children.TryGetValue(node, out var existing) && existing is YamlSequenceNode s ? s : new YamlSequenceNode();
        sequence.Add(new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted });
        service.Children[node] = sequence;
    }

    private void AddSharedNetwork(YamlMappingNode root)
    {
        var key = new YamlScalarNode("networks");
        var networks = root.Children.TryGetValue(key, out var existing) && existing is YamlMappingNode m ? m : new YamlMappingNode();
        networks.Children[new YamlScalarNode(_options.NetworkName)] = new YamlMappingNode
        {
            { "name", _options.NetworkName },
            { "external", new YamlScalarNode("true") },
        };
        root.Children[key] = networks;
    }

    private static void SetMapping(YamlMappingNode root, string key, IReadOnlyDictionary<string, string> values)
    {
        var map = new YamlMappingNode();
        foreach (var (name, value) in values.Where(v => !string.IsNullOrWhiteSpace(v.Value)))
        {
            map.Children[new YamlScalarNode(name)] = new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
        }

        root.Children[new YamlScalarNode(key)] = map;
    }
}
