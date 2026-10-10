using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Entities;

namespace Mattiverse.Core.Apps;

/// <summary>
/// The files of an app: the rendered compose file and the <c>.env</c> with the answers of the install form. They are what the
/// stack engine deploys, and what Mattiverse writes to <c>apps/&lt;slug&gt;/</c> on the data volume when it runs the stack itself.
/// </summary>
public sealed class ComposeFiles
{
    public const string ComposeFile = "docker-compose.yml";
    public const string EnvFile = ".env";

    private readonly AppPaths _paths;
    private readonly ComposeRenderer _renderer;

    public ComposeFiles(AppPaths paths, ComposeRenderer renderer)
    {
        _paths = paths;
        _renderer = renderer;
    }

    public string Folder(string slug) => _paths.AppFolder(slug);

    public string ProjectName(string slug) => _renderer.ProjectName(slug);

    public string ContainerName(string slug, string service) => _renderer.ContainerName(slug, service);

    public string UiHost(InstalledApp app) => _renderer.UiHost(app);

    /// <summary>What an engine deploys for an app: the compose text and the <c>.env</c> text.</summary>
    public StackSpec Build(InstalledApp app, CatalogApp catalog, IReadOnlyDictionary<string, string> variables, string publicHost)
        => new(app.ProjectName, app.Slug, _renderer.Render(catalog, app), AppVariables.BuildEnvFile(BuiltIns(app, publicHost), variables));

    public async Task WriteAsync(StackSpec spec, CancellationToken ct = default)
    {
        var folder = Folder(spec.Slug);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, ComposeFile), spec.Compose, ct);
        await File.WriteAllTextAsync(Path.Combine(folder, EnvFile), spec.Env, ct);
    }

    public bool Exists(string slug) => File.Exists(Path.Combine(Folder(slug), ComposeFile));

    public async Task<StackSpec?> ReadAsync(string slug, string project, CancellationToken ct = default)
    {
        var folder = Folder(slug);
        if (!File.Exists(Path.Combine(folder, ComposeFile)))
        {
            return null;
        }

        return new StackSpec(
            project,
            slug,
            await File.ReadAllTextAsync(Path.Combine(folder, ComposeFile), ct),
            File.Exists(Path.Combine(folder, EnvFile)) ? await File.ReadAllTextAsync(Path.Combine(folder, EnvFile), ct) : string.Empty);
    }

    public void DeleteFolder(string slug)
    {
        var folder = Folder(slug);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Values every app can use as <c>${...}</c> in its compose file.</summary>
    private static Dictionary<string, string> BuiltIns(InstalledApp app, string publicHost) => new()
    {
        ["MV_SLUG"] = app.Slug,
        ["MV_PROJECT"] = app.ProjectName,
        ["MV_PUBLIC_HOST"] = publicHost,
        ["TZ"] = Environment.GetEnvironmentVariable("TZ") is { Length: > 0 } tz ? tz : "UTC",
    };
}

/// <summary>An app as a stack: the name of the compose project, the app's slug, the compose text and the <c>.env</c> text.</summary>
public sealed record StackSpec(string Name, string Slug, string Compose, string Env);
