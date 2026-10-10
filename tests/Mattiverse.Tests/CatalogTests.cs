using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mattiverse.Tests;

public class CatalogTests
{
    private static CatalogService CreateCatalog(string? dataPath = null)
        => new(new AppPaths(dataPath ?? Path.Combine(Path.GetTempPath(), "mattiverse-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);

    [Fact]
    public void BuiltInCatalog_LoadsEveryEntry()
    {
        var apps = CreateCatalog().GetAll();

        // One file per entry: a broken file is skipped (and logged), so the count would drop.
        var files = typeof(CatalogService).Assembly.GetManifestResourceNames().Count(n => n.StartsWith("catalog/"));
        Assert.Equal(files, apps.Count);
        Assert.NotEmpty(apps);
    }

    [Fact]
    public void BuiltInCatalog_HasTheWizardRoles()
    {
        var catalog = CreateCatalog();

        Assert.Contains(catalog.GetByRole(AppRoles.Proxy), a => a.Id == "matcad");
        Assert.Contains(catalog.GetByRole(AppRoles.Ddns), a => a.Id == "matddns");
        Assert.Contains(catalog.GetByRole(AppRoles.Docker), a => a.Id == "matdock");
        Assert.True(catalog.GetByRole(AppRoles.Proxy)[0].Recommended);
    }

    [Fact]
    public void BuiltInCatalog_EntriesAreConsistent()
    {
        foreach (var app in CreateCatalog().GetAll())
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Tagline), $"{app.Id}: tagline");
            Assert.True(app.Ui is null || app.ServiceNames.Contains(app.Ui.Service), $"{app.Id}: ui service");

            // The UI port is published by Mattiverse itself; the compose file must not claim a host port for it.
            if (app.Ui is { } ui)
            {
                Assert.DoesNotContain(app.FixedPorts, p => p.HostPort == ui.HostPort && ui.HostPort is not null && p.Raw.EndsWith($":{ui.Port}", StringComparison.Ordinal));
            }

            foreach (var variable in app.Variables)
            {
                Assert.False(string.IsNullOrWhiteSpace(variable.Key), $"{app.Id}: variable key");
                Assert.True(app.ComposeYaml.Contains("${" + variable.Key, StringComparison.Ordinal), $"{app.Id}: variable {variable.Key} is not used in the compose file");
            }

            // Every ${KEY} of the compose file must be answered by a variable (or carry a default), or compose would run with an empty value.
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(app.ComposeYaml, @"\$\{(?<key>[A-Z_][A-Z0-9_]*)(?<default>:?-[^}]*)?\}"))
            {
                var key = match.Groups["key"].Value;
                var known = app.Variables.Any(v => v.Key == key) || key is "MV_SLUG" or "MV_PROJECT" or "MV_PUBLIC_HOST" or "TZ";
                Assert.True(known || match.Groups["default"].Success, $"{app.Id}: ${{{key}}} has no variable and no default");
            }
        }
    }

    [Fact]
    public void Catalog_ReadsUserFilesFromTheDataFolder_AndTheyReplaceBuiltIns()
    {
        var data = Path.Combine(Path.GetTempPath(), "mattiverse-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(data);
        paths.EnsureCreated();
        File.WriteAllText(Path.Combine(paths.CatalogPath, "whoami.yml"), """
            x-mattiverse:
              id: whoami
              name: Whoami
              tagline: Test
              ui: { service: web, port: 80 }
            services:
              web:
                image: traefik/whoami
            """);
        File.WriteAllText(Path.Combine(paths.CatalogPath, "matcad.yml"), """
            x-mattiverse:
              id: matcad
              name: Custom Matcad
              tagline: Replaced
              role: proxy
            services:
              caddy:
                image: caddy
            """);
        File.WriteAllText(Path.Combine(paths.CatalogPath, "broken.yml"), "services: [");

        var catalog = new CatalogService(paths, NullLogger<CatalogService>.Instance);

        Assert.NotNull(catalog.Get("whoami"));
        Assert.False(catalog.Get("whoami")!.BuiltIn);
        Assert.Equal("Custom Matcad", catalog.Get("matcad")!.Name);
        Assert.Null(catalog.Get("broken"));
    }

    [Theory]
    [InlineData("services:\n  a:\n    image: x\n", "x-mattiverse")]
    [InlineData("x-mattiverse:\n  id: Bad Id\n  name: X\nservices:\n  a:\n    image: x\n", "'id'")]
    [InlineData("x-mattiverse:\n  id: ok\n  name: X\n  ui: { service: nope, port: 80 }\nservices:\n  a:\n    image: x\n", "ui.service")]
    [InlineData("x-mattiverse:\n  id: ok\n  name: X\n", "services")]
    public void Parser_RejectsInvalidEntries(string yaml, string expectedMessagePart)
    {
        var ex = Assert.Throws<FormatException>(() => CatalogParser.Parse(yaml, "test.yml"));
        Assert.Contains(expectedMessagePart, ex.Message);
    }

    [Theory]
    [InlineData("80:80", 80, "tcp")]
    [InlineData("443:443/udp", 443, "udp")]
    [InlineData("127.0.0.1:9293:9293", 9293, "tcp")]
    [InlineData("${SMTP_PORT:-25}:25", 25, "tcp")]
    public void ParsePort_ReadsTheHostSide(string text, int host, string protocol)
    {
        var port = CatalogParser.ParsePort(text);

        Assert.NotNull(port);
        Assert.Equal(host, port!.HostPort);
        Assert.Equal(protocol, port.Protocol);
    }

    [Fact]
    public void ParsePort_IgnoresAnEphemeralHostPort() => Assert.Null(CatalogParser.ParsePort("8080"));

    [Fact]
    public void MatMail_PublishesItsMailPortsAsFixedPorts()
    {
        var matmail = CreateCatalog().Get("matmail")!;

        Assert.Equal(new[] { 25, 587, 465, 143, 993 }, matmail.FixedPorts.Select(p => p.HostPort).ToArray());
    }
}
