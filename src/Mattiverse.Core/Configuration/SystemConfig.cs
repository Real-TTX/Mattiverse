namespace Mattiverse.Core.Configuration;

/// <summary>
/// The main configuration (<c>config/system.json</c>): instance settings and the choices made in the setup wizard.
/// </summary>
public sealed class SystemConfig
{
    public const string FileName = "system";

    /// <summary>Shown in the browser title and on the home screen.</summary>
    public string InstanceName { get; set; } = "Mattiverse";

    /// <summary>
    /// Public base domain for the apps (e.g. <c>example.com</c>). When a reverse proxy is installed every app gets
    /// <c>&lt;app&gt;.&lt;BaseDomain&gt;</c>. Empty = apps are only reachable through their host port.
    /// </summary>
    public string? BaseDomain { get; set; }

    /// <summary>The address under which the user reaches this host (used to build "open" links), e.g. <c>nas.local</c>.
    /// Empty = the host name of the browser request.</summary>
    public string? PublicHost { get; set; }

    /// <summary>True once the user has been through (or has skipped) the setup wizard.</summary>
    public bool WizardCompleted { get; set; }

    public DateTime? WizardCompletedAt { get; set; }

    /// <summary>The wizard's choices; <c>null</c> = "none / I have one already".</summary>
    public WizardSelection Selection { get; set; } = new();

    /// <summary>Per-integration secrets (API keys); values are protected with Data Protection.</summary>
    public Dictionary<string, string> ProtectedSecrets { get; set; } = new();

    public BackupDefaults Backups { get; set; } = new();

    /// <summary>Default theme for new users: system, light or dark.</summary>
    public string DefaultTheme { get; set; } = "system";
}

/// <summary>The three infrastructure questions of the wizard.</summary>
public sealed class WizardSelection
{
    /// <summary>Catalog id of the reverse proxy (e.g. <c>matcad</c>), or <c>null</c>.</summary>
    public string? ProxyAppId { get; set; }

    /// <summary>Catalog id of the DynDNS client (e.g. <c>matddns</c>), or <c>null</c>.</summary>
    public string? DdnsAppId { get; set; }

    /// <summary>Catalog id of the advanced Docker management (e.g. <c>matdock</c>), or <c>null</c>.</summary>
    public string? DockerAppId { get; set; }
}

public sealed class BackupDefaults
{
    /// <summary>How many backups are kept per app by a schedule.</summary>
    public int Retention { get; set; } = 7;

    /// <summary>Stop the app's containers while its volumes are copied (consistent databases).</summary>
    public bool StopContainers { get; set; } = true;
}
