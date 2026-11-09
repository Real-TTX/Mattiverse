using Mattiverse.Core.Apps;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Engines;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Jobs;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages;

/// <summary>The home screen: a launcher with the installed apps as icons, a few widgets, and the parts of Mattiverse as apps (on the screen and in the dock).</summary>
public class IndexModel : PageModel
{
    /// <summary>A backup older than this is worth a look (the widget turns amber).</summary>
    public static readonly TimeSpan BackupIsOld = TimeSpan.FromDays(7);

    private readonly AppService _apps;
    private readonly SystemConfigService _config;
    private readonly JobService _jobs;
    private readonly DockerService _docker;
    private readonly BackupService _backups;
    private readonly CatalogService _catalog;
    private readonly AppUrls _urls;
    private readonly EngineOverview _engines;

    public IndexModel(AppService apps, SystemConfigService config, JobService jobs, DockerService docker, BackupService backups, CatalogService catalog, AppUrls urls, EngineOverview engines)
    {
        _engines = engines;
        _apps = apps;
        _config = config;
        _jobs = jobs;
        _docker = docker;
        _backups = backups;
        _catalog = catalog;
        _urls = urls;
    }

    /// <summary>An installed app on the home screen.</summary>
    /// <param name="App">The app.</param>
    /// <param name="Status">Its state (the badge on the corner).</param>
    /// <param name="Window">What the window of the home screen shows: the app in its frame (<c>/Apps/Open</c>), or its details when it has no web page or is not installed.</param>
    /// <param name="Tab">The address of the app itself, for a new tab (the default of an app), or <c>null</c> when it has none.</param>
    /// <param name="Details">Its page in Mattiverse.</param>
    public sealed record Tile(InstalledApp App, AppStatusView Status, string Window, string? Tab, string Details);

    public string? IconOf(InstalledApp app) => _catalog.IconOf(app.CatalogId);

    public SystemConfig Config { get; private set; } = new();
    public List<Tile> Tiles { get; private set; } = new();
    public DockerEngineInfo Engine { get; private set; } = new(false, null, string.Empty, string.Empty, 0, 0, 0, 0, 0);
    public int Running { get; private set; }
    public int ActiveJobs { get; private set; }
    public DateTime? LastBackup { get; private set; }
    public bool Busy => Tiles.Any(t => t.App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing);

    /// <summary>No backup at all, or the newest one is older than <see cref="BackupIsOld"/>.</summary>
    public bool BackupNeedsLook => LastBackup is not { } last || DateTime.UtcNow - last > BackupIsOld;

    /// <summary>An engine on the home screen: its row, and its state from Docker (<c>null</c> when it is not installed). Its window is <see cref="EngineRow.Window"/>.</summary>
    public sealed record EngineTile(EngineRow Row, AppStatusView? Status);

    /// <summary>The five engines: in the folder System and in their widget (an administrator's), instead of between the apps.</summary>
    public IReadOnlyList<EngineTile> Engines { get; private set; } = [];

    public IReadOnlyList<SystemApp> Dock { get; private set; } = [];
    public IReadOnlyList<SystemApp> SystemApps { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var isAdmin = User.IsAdmin();
        Config = await _config.GetAsync(ct);
        Engine = await _docker.GetEngineInfoAsync(ct);

        var apps = (await _apps.ListAsync(ct)).Where(a => a.Pinned).ToList();
        var runtimes = Engine.Available ? await _apps.GetRuntimesAsync(ct) : new Dictionary<string, RuntimeInfo>();

        Tiles = new List<Tile>(apps.Count);
        foreach (var a in apps.Where(a => !(isAdmin && EngineIds.IsEngine(a.CatalogId))))
        {
            var runtime = runtimes.GetValueOrDefault(a.Slug);
            var details = $"/Apps/Details?id={a.Id}";
            var opens = a.Lifecycle == AppLifecycle.Installed && a.UiService is not null;
            var tab = opens ? await _urls.OpenUrlAsync(a, Request, ct) : null;
            Tiles.Add(new Tile(a, AppStatus.Of(a, runtime), opens ? $"/Apps/Open?id={a.Id}" : details, tab, details));
        }

        Running = Tiles.Count(t => t.Status.Css == "online");

        if (isAdmin)
        {
            ActiveJobs = await _jobs.CountActiveAsync(ct);
            LastBackup = await _backups.LastSucceededAtAsync(ct);
        }

        if (isAdmin)
        {
            // The state from Docker, which the apps had already: the widget and the folder ask no engine (the window of one does).
            Engines = (await _engines.ListAsync(Request, askApi: false, ct))
                .Select(r => new EngineTile(r, r.App is null ? null : AppStatus.Of(r.App, runtimes.GetValueOrDefault(r.App.Slug))))
                .ToList();
        }

        Dock = Launcher.Dock(isAdmin);
        SystemApps = Launcher.OnScreen(isAdmin, ActiveJobs);
    }
}
