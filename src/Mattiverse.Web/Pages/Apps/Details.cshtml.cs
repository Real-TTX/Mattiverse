using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Jobs;
using Mattiverse.Core.Security;
using Mattiverse.Web.Controls;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages.Apps;

public class DetailsModel : PageModel
{
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly DockerService _docker;
    private readonly MattiverseDbContext _db;
    private readonly ComposeFiles _files;
    private readonly JobService _jobs;
    private readonly ISecretProtector _protector;
    private readonly AppUrls _urls;

    public DetailsModel(
        AppService apps,
        CatalogService catalog,
        DockerService docker,
        MattiverseDbContext db,
        ComposeFiles files,
        JobService jobs,
        ISecretProtector protector,
        AppUrls urls)
    {
        _apps = apps;
        _catalog = catalog;
        _docker = docker;
        _db = db;
        _files = files;
        _jobs = jobs;
        _protector = protector;
        _urls = urls;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    /// <summary>The container whose log is shown on the Logs tab.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Container { get; set; }

    public InstalledApp App { get; private set; } = null!;
    public CatalogApp? Catalog { get; private set; }
    public AppStatusView Status { get; private set; } = new("unknown", "Unknown");
    public RuntimeInfo Runtime { get; private set; } = new(RuntimeState.Missing, 0, 0, string.Empty);
    public List<ContainerInfo> Containers { get; private set; } = new();
    public string? OpenUrl { get; private set; }
    public string InternalAddress { get; private set; } = string.Empty;
    public Job? ActiveJob { get; private set; }

    public List<CredentialLine> Credentials { get; private set; } = new();
    public List<Backup> Backups { get; private set; } = new();
    public List<ShareLink> Shares { get; private set; } = new();
    public string LogText { get; private set; } = string.Empty;
    public (string Compose, string Env, string Override) Files { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    public string ActiveTab => Tab switch { "logs" => "logs", "backups" => "backups", "files" => "files", "sharing" => "sharing", _ => "overview" };

    public TabBarModel Tabs
    {
        get
        {
            var tabs = new List<TabItem>
            {
                new() { Title = "Overview", Href = $"/Apps/Details?id={Id}", Icon = "info", Active = ActiveTab == "overview" },
                new() { Title = "Logs", Href = $"/Apps/Details?id={Id}&tab=logs", Icon = "terminal", Active = ActiveTab == "logs" },
            };
            if (User.IsAdmin())
            {
                tabs.Add(new() { Title = "Backups", Href = $"/Apps/Details?id={Id}&tab=backups", Icon = "save", Active = ActiveTab == "backups" });
                tabs.Add(new() { Title = "Files", Href = $"/Apps/Details?id={Id}&tab=files", Icon = "file", Active = ActiveTab == "files" });
                tabs.Add(new() { Title = "Sharing", Href = $"/Apps/Details?id={Id}&tab=sharing", Icon = "link", Active = ActiveTab == "sharing" });
            }

            return new TabBarModel { Tabs = tabs };
        }
    }

    public bool CanOpen => App.Lifecycle == AppLifecycle.Installed && App.UiService is not null;
    public bool IsBusy => App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing || ActiveJob is not null;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var ct = HttpContext.RequestAborted;
        if (User.IsAdmin())
        {
            Credentials = AppCredentials.Deserialize(App.CredentialsJson, _protector);
        }

        switch (ActiveTab)
        {
            case "logs":
                var container = Containers.FirstOrDefault(c => c.Name == Container) ?? Containers.FirstOrDefault();
                Container = container?.Name;
                LogText = container is null ? "The app has no containers." : await _docker.GetLogsAsync(container.Name, 400, ct);
                break;
            case "backups" when User.IsAdmin():
                Backups = await _db.Backups.AsNoTracking().Where(b => b.AppSlug == App.Slug).OrderByDescending(b => b.CreateDate).Take(50).ToListAsync(ct);
                break;
            case "files" when User.IsAdmin():
                var (compose, env, overrideYaml) = await _files.ReadAsync(App.Slug, ct);
                Files = (compose, MaskSecrets(env), overrideYaml);
                break;
            case "sharing" when User.IsAdmin():
                Shares = await _db.ShareLinks.AsNoTracking().Where(s => s.AppId == App.Id).OrderByDescending(s => s.CreateDate).ToListAsync(ct);
                break;
        }

        return Page();
    }

    public Task<IActionResult> OnPostStartAsync() => LifecycleAsync("start", "Start");

    public Task<IActionResult> OnPostStopAsync() => LifecycleAsync("stop", "Stop");

    public Task<IActionResult> OnPostRestartAsync() => LifecycleAsync("restart", "Restart");

    /// <summary>Runs the install again for an app whose install (or restore) failed: pull, start, wait, set up.</summary>
    public async Task<IActionResult> OnPostRetryAsync()
    {
        if (!User.IsAdmin())
        {
            return Forbid();
        }

        var app = await _apps.GetAsync(Id, HttpContext.RequestAborted);
        if (app is null || app.Lifecycle != AppLifecycle.Failed)
        {
            return NotFound();
        }

        app.Lifecycle = AppLifecycle.Installing;
        app.StatusMessage = null;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        var jobId = await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name} (retry)", app, AppJobs.Install(app.Id), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    public async Task<IActionResult> OnPostDeleteShareAsync(long shareId)
    {
        if (!User.IsAdmin())
        {
            return Forbid();
        }

        var link = await _db.ShareLinks.FirstOrDefaultAsync(s => s.Id == shareId && s.AppId == Id, HttpContext.RequestAborted);
        if (link is not null)
        {
            _db.ShareLinks.Remove(link);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            StatusMessage = "The link no longer works.";
        }

        return RedirectToPage(new { id = Id, tab = "sharing" });
    }

    public string ShareUrl(ShareLink link) => $"{Request.Scheme}://{Request.Host}/s/{link.Token}";

    private async Task<IActionResult> LifecycleAsync(string command, string label)
    {
        if (!User.IsAdmin())
        {
            return Forbid();
        }

        var app = await _apps.GetAsync(Id, HttpContext.RequestAborted);
        if (app is null)
        {
            return NotFound();
        }

        var jobId = await _jobs.EnqueueAsync(Enum.Parse<JobKind>(label), $"{label} {app.Name}", app, AppJobs.Lifecycle(app.Id, command), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var app = await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id, ct);
        if (app is null)
        {
            return false;
        }

        App = app;
        Catalog = _catalog.Get(app.CatalogId);
        Containers = await _docker.ListContainersAsync(app.Slug, ct);
        Runtime = DockerService.Summarize(Containers);
        Status = AppStatus.Of(app, Runtime);
        OpenUrl = await _urls.OpenUrlAsync(app, Request, ct);
        InternalAddress = app.UiService is null ? string.Empty : $"{_files.UiHost(app)}:{app.UiPort}";
        ActiveJob = await _jobs.GetActiveAsync(app.Id, ct);
        return true;
    }

    /// <summary>The <c>.env</c> of an app holds its secrets; the page shows the keys and hides the values of secret variables.</summary>
    private string MaskSecrets(string env)
    {
        var secretKeys = Catalog?.Variables.Where(v => v.IsSecret).Select(v => v.Key).ToHashSet() ?? [];
        var lines = env.Split('\n').Select(line =>
        {
            var eq = line.IndexOf('=');
            return eq > 0 && secretKeys.Contains(line[..eq].Trim()) ? line[..(eq + 1)] + "'••••••••'" : line;
        });
        return string.Join('\n', lines);
    }
}
