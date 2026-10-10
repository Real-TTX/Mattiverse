using YamlDotNet.Serialization;

namespace Mattiverse.Core.Catalog;

/// <summary>What an app is for in the setup wizard.</summary>
public static class AppRoles
{
    public const string Proxy = "proxy";
    public const string Ddns = "ddns";
    public const string Docker = "docker";
    public const string App = "app";
}

/// <summary>
/// One entry of the app catalog. A catalog entry is a normal <c>docker compose</c> file plus a top-level
/// <c>x-mattiverse</c> block with what the store and the installer need (name, icon, which service has the UI, the
/// install questions). Compose ignores <c>x-*</c> keys, so the same file also runs with plain <c>docker compose</c>.
/// </summary>
public sealed class CatalogApp
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Tagline { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = "Apps";

    /// <summary>One of <see cref="AppRoles"/>.</summary>
    public string Role { get; set; } = AppRoles.App;

    /// <summary>File name below <c>wwwroot/img/apps/</c> or an absolute URL. Empty = a generated tile.</summary>
    public string? Icon { get; set; }

    public string? Homepage { get; set; }

    /// <summary>Offered first in the setup wizard.</summary>
    public bool Recommended { get; set; }

    /// <summary>Short notes shown on the install page (limits, hints).</summary>
    public List<string> Notes { get; set; } = new();

    public CatalogUi? Ui { get; set; }

    /// <summary>Services that are also attached to the shared network (the UI service always is).</summary>
    public List<string> Shared { get; set; } = new();

    /// <summary>Key of a built-in provisioner that runs after the install (for example <c>matcad</c>).</summary>
    public string? Provision { get; set; }

    public List<CatalogVariable> Variables { get; set; } = new();

    public List<CatalogCredential> Credentials { get; set; } = new();

    // ---- filled by the loader, not part of the YAML block ----

    [YamlIgnore]
    public string ComposeYaml { get; set; } = string.Empty;

    [YamlIgnore]
    public List<string> ServiceNames { get; set; } = new();

    /// <summary>Host ports that the compose file itself publishes (for example 80/443 of a reverse proxy).</summary>
    [YamlIgnore]
    public List<CatalogPort> FixedPorts { get; set; } = new();

    [YamlIgnore]
    public bool BuiltIn { get; set; } = true;

    [YamlIgnore]
    public string Source { get; set; } = "built-in";
}

public sealed class CatalogUi
{
    /// <summary>The compose service that serves the web UI.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>The port inside that container.</summary>
    public int Port { get; set; }

    /// <summary>The host port Mattiverse tries first; a free one from the pool is used when it is taken.</summary>
    public int? HostPort { get; set; }

    /// <summary>Appended to the address when the app is opened (for example <c>/login</c>).</summary>
    public string? Path { get; set; }
}

/// <summary>One question of the install form; the answer reaches compose as <c>${KEY}</c> from the <c>.env</c> file.</summary>
public sealed class CatalogVariable
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary><c>text</c>, <c>secret</c>, <c>number</c>.</summary>
    public string Type { get; set; } = "text";

    public string? Default { get; set; }

    public string? Help { get; set; }

    public bool Required { get; set; }

    /// <summary>Generate a random value when the field is left empty (passwords, keys).</summary>
    public bool Generate { get; set; }

    /// <summary>Not shown in the form at all: always generated or defaulted (API keys).</summary>
    public bool Hidden { get; set; }

    /// <summary>Shown below "Advanced" instead of the main form.</summary>
    public bool Advanced { get; set; }

    [YamlIgnore]
    public bool IsSecret => Type.Equals("secret", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An entry of the "credentials" card of an app (initial logins). <c>${KEY}</c> is replaced by the variable's value.</summary>
public sealed class CatalogCredential
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Secret { get; set; }
}

public sealed class CatalogPort
{
    public string Raw { get; set; } = string.Empty;

    public int HostPort { get; set; }

    public string Protocol { get; set; } = "tcp";
}
