using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mattiverse.Tests;

/// <summary>The platform contract and the single sign-on in the catalog: the variables Mattiverse fills itself, and the entries of MatIdentity and MatSplit.</summary>
public class PlatformTests
{
    private const string Minimal = """
        x-mattiverse:
          id: demo
          name: Demo
          ui:
            service: web
            port: 8080
          platform: true
          identity: {}
        services:
          web:
            image: nginx
        """;

    private static CatalogService CreateCatalog()
        => new(new AppPaths(Path.Combine(Path.GetTempPath(), "mattiverse-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);

    [Fact]
    public void An_app_of_the_platform_gets_its_key_and_the_sign_in_variables_hidden()
    {
        var app = CatalogParser.Parse(Minimal, "test");

        var key = Assert.Single(app.Variables, v => v.Key == ManagedVariables.ApiKey);
        Assert.True(key.Hidden && key.Generate && key.IsSecret);
        Assert.Contains(app.Variables, v => v.Key == ManagedVariables.IdentityAuthority && v.Hidden && !v.IsSecret);
        Assert.Contains(app.Variables, v => v.Key == ManagedVariables.IdentityInternalUrl && v.Hidden);
        Assert.Contains(app.Variables, v => v.Key == ManagedVariables.IdentityClientId && v.Hidden);
        Assert.Contains(app.Variables, v => v.Key == ManagedVariables.IdentityClientSecret && v.Hidden && v.IsSecret);
        Assert.Equal("/signin-oidc", app.Identity!.Callback);
        Assert.Equal("/signout-callback-oidc", app.Identity.SignedOut);
    }

    [Fact]
    public void An_app_without_the_platform_gets_none_of_them()
    {
        var app = CatalogParser.Parse(Minimal.Replace("  platform: true\n", string.Empty).Replace("  identity: {}\n", string.Empty), "test");
        Assert.DoesNotContain(app.Variables, v => v.Key.StartsWith("MAT", StringComparison.Ordinal));
    }

    [Fact]
    public void Platform_and_identity_need_a_web_service()
        => Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("  ui:\n    service: web\n    port: 8080\n", string.Empty), "test"));

    [Fact]
    public void MatIdentity_is_in_the_catalog_as_the_single_sign_on()
    {
        var identity = CreateCatalog().Get(MatIdentityClient.CatalogIdValue);
        Assert.NotNull(identity);
        Assert.Equal(AppRoles.Identity, identity.Role);
        Assert.Equal("matidentity", identity.Provision);
        Assert.True(identity.Platform);
        Assert.Contains(identity.Variables, v => v.Key == ManagedVariables.IdentityIssuer);
        Assert.Contains("MatIdentity__Issuer: ${MATIDENTITY_ISSUER:-}", identity.ComposeYaml);
        Assert.Contains("MAT_API_KEY: ${MAT_API_KEY}", identity.ComposeYaml);
    }

    [Theory]
    [InlineData("matsplit")]
    [InlineData("mattrakr")]
    [InlineData("matplay")]
    [InlineData("matbu")]
    [InlineData("matmail")]
    [InlineData("matpaper")]
    public void The_app_speaks_the_platform_and_signs_in_through_MatIdentity(string id)
    {
        var app = CreateCatalog().Get(id);
        Assert.NotNull(app);
        Assert.True(app.Platform);
        Assert.NotNull(app.Identity);
        foreach (var key in new[] { ManagedVariables.ApiKey, ManagedVariables.IdentityAuthority, ManagedVariables.IdentityInternalUrl, ManagedVariables.IdentityClientId, ManagedVariables.IdentityClientSecret })
        {
            Assert.Contains($"{key}: ${{{key}", app.ComposeYaml);
        }
    }

    [Theory]
    [InlineData("matsplit", "matsplit")]
    [InlineData("a", "a-app")]
    public void The_client_id_of_an_app_is_its_slug(string slug, string clientId)
        => Assert.Equal(clientId, IdentityLink.ClientIdOf(new InstalledApp { Slug = slug }));
}
