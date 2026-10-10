using Mattiverse.Core.Apps;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Security;
using Microsoft.Extensions.Options;

namespace Mattiverse.Tests;

public class AppFilesTests
{
    private static readonly IOptions<MattiverseOptions> Options = Microsoft.Extensions.Options.Options.Create(new MattiverseOptions());

    private static ComposeFiles CreateFiles(string? data = null)
        => new(new AppPaths(data ?? Path.Combine(Path.GetTempPath(), "mattiverse-tests-" + Guid.NewGuid().ToString("N"))), Options);

    private static CatalogApp Matcad() => CatalogParser.Parse(ReadCatalog("matcad"), "matcad.yml");

    private static string ReadCatalog(string id)
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream($"catalog/{id}.yml")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Override_NamesEveryContainer_AndLabelsIt()
    {
        var files = CreateFiles();
        var app = new InstalledApp { Slug = "matcad", UiService = "matcad", UiPort = 4433, HostPort = 4433 };

        var yaml = files.BuildOverride(app, Matcad());

        Assert.Contains("container_name: mattiverse-matcad-caddy", yaml);
        Assert.Contains("container_name: mattiverse-matcad-matcad", yaml);
        Assert.Contains("mattiverse.instance: \"matcad\"", yaml);
        Assert.Contains("mattiverse.service: \"caddy\"", yaml);
    }

    [Fact]
    public void Override_PublishesOnlyTheUiPort_AndJoinsTheSharedNetwork()
    {
        var files = CreateFiles();
        var app = new InstalledApp { Slug = "matcad", UiService = "matcad", UiPort = 4433, HostPort = 4500 };

        var yaml = files.BuildOverride(app, Matcad());

        Assert.Equal(1, CountOccurrences(yaml, "ports:"));
        Assert.Contains("- \"4500:4433\"", yaml);
        // Caddy is listed as shared in the catalog entry, the UI service always is.
        Assert.Equal(2, CountOccurrences(yaml, "- mattiverse\n"));
        Assert.Contains("external: true", yaml);
    }

    [Fact]
    public void Override_KeepsAServiceOffTheSharedNetwork_UnlessItIsShared()
    {
        var matmail = CatalogParser.Parse(ReadCatalog("matmail"), "matmail.yml");
        var files = CreateFiles();
        var app = new InstalledApp { Slug = "matmail", UiService = "matmail", UiPort = 9933, HostPort = 9933 };

        var yaml = files.BuildOverride(app, matmail);

        // Only the UI service joins the shared network; the database stays private to the stack.
        Assert.Equal(1, CountOccurrences(yaml, "- mattiverse\n"));
    }

    [Fact]
    public void ProjectAndContainerNames_UseThePrefix()
    {
        var files = CreateFiles();

        Assert.Equal("mattiverse-matcad", files.ProjectName("matcad"));
        Assert.Equal("mattiverse-matcad-caddy", files.ContainerName("matcad", "caddy"));
    }

    [Fact]
    public async Task WriteAsync_WritesComposeEnvAndOverride()
    {
        var data = Path.Combine(Path.GetTempPath(), "mattiverse-tests-" + Guid.NewGuid().ToString("N"));
        var files = CreateFiles(data);
        var catalog = Matcad();
        var app = new InstalledApp { Slug = "matcad", ProjectName = "mattiverse-matcad", UiService = "matcad", UiPort = 4433, HostPort = 4433 };

        await files.WriteAsync(app, catalog, new Dictionary<string, string> { ["MATCAD_API_KEY"] = "k3y" }, "nas.local");

        var (compose, env, overrideYaml) = await files.ReadAsync("matcad");
        Assert.Equal(catalog.ComposeYaml, compose);
        Assert.Contains("MATCAD_API_KEY='k3y'", env);
        Assert.Contains("MV_PUBLIC_HOST='nas.local'", env);
        Assert.Contains("MV_SLUG='matcad'", env);
        Assert.Contains("container_name", overrideYaml);
        Assert.True(files.Exists("matcad"));
    }

    [Fact]
    public void EnvFile_RoundTrips_AndQuotesSpecialCharacters()
    {
        var env = AppVariables.BuildEnvFile(
            new Dictionary<string, string> { ["MV_SLUG"] = "x" },
            new Dictionary<string, string> { ["PASSWORD"] = "pa$$ w#rd" });

        var parsed = EnvFileParser.Parse(env);

        Assert.Contains("PASSWORD='pa$$ w#rd'", env);
        Assert.Equal("pa$$ w#rd", parsed["PASSWORD"]);
        Assert.True(EnvFileParser.IsBuiltIn("MV_SLUG"));
        Assert.False(EnvFileParser.IsBuiltIn("PASSWORD"));
    }

    private static int CountOccurrences(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
