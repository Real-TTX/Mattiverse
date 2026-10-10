using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Jobs;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages;

/// <summary>The home screen: a launcher with the installed apps as icons, a few widgets, and shortcuts to the system apps.</summary>
public class IndexModel : PageModel
{
    private readonly AppService _apps;
    private readonly SystemConfigService _config;
    private readonly JobService _jobs;
    private readonly DockerService _docker;
    private readonly MattiverseDbContext _db;
    private readonly CatalogService _catalog;

    public IndexModel(AppService apps, SystemConfigService config, JobService jobs, DockerService docker, MattiverseDbContext db, CatalogService catalog)
    {
        _apps = apps;
        _config = config;
        _jobs = jobs;
        _docker = docker;
        _db = db;
        _catalog = catalog;
    }

    public sealed record Tile(InstalledApp App, AppStatusView Status, string Href);

    public string? IconOf(InstalledApp app) => _catalog.IconOf(app.CatalogId);

    public SystemConfig Config { get; private set; } = new();
    public List<Tile> Tiles { get; private set; } = new();
    public DockerEngineInfo Engine { get; private set; } = new(false, null, string.Empty, string.Empty, 0, 0, 0, 0, 0);
    public int Running { get; private set; }
    public int ActiveJobs { get; private set; }
    public DateTime? LastBackup { get; private set; }
    public bool Busy => Tiles.Any(t => t.App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing);

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Config = await _config.GetAsync(ct);
        Engine = await _docker.GetEngineInfoAsync(ct);

        var apps = (await _apps.ListAsync(ct)).Where(a => a.Pinned).ToList();
        var runtimes = Engine.Available ? await _apps.GetRuntimesAsync(ct) : new Dictionary<string, RuntimeInfo>();

        Tiles = apps
            .Select(a =>
            {
                var runtime = runtimes.GetValueOrDefault(a.Slug);
                var opens = a.Lifecycle == AppLifecycle.Installed && a.UiService is not null;
                return new Tile(a, AppStatus.Of(a, runtime), opens ? $"/Apps/Open?id={a.Id}" : $"/Apps/Details?id={a.Id}");
            })
            .ToList();

        Running = Tiles.Count(t => t.Status.Css == "online");

        if (User.IsAdmin())
        {
            ActiveJobs = await _jobs.CountActiveAsync(ct);
            LastBackup = await _db.Backups.Where(b => b.Status == BackupStatus.Succeeded)
                .OrderByDescending(b => b.CreateDate)
                .Select(b => (DateTime?)b.CreateDate)
                .FirstOrDefaultAsync(ct);
        }
    }
}
