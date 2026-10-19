using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Engines;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Integrations;
using Mattiverse.Core.Jobs;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Settings;

/// <summary>MatDock, Matcad, Matddns, MatIdentity and MatSMTP: which of them are there, whether their API answers, and where to open them.</summary>
public class EnginesModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly MatDockClient _matdock;
    private readonly MatcadClient _matcad;
    private readonly MatddnsClient _matddns;
    private readonly MatIdentityClient _matidentity;
    private readonly MatSmtpClient _matsmtp;
    private readonly MatDockEngine _matdockEngine;
    private readonly AppReconciler _reconciler;
    private readonly AppUrls _urls;
    private readonly JobService _jobs;

    public EnginesModel(
        SystemConfigService config,
        AppService apps,
        CatalogService catalog,
        MatDockClient matdock,
        MatcadClient matcad,
        MatddnsClient matddns,
        MatIdentityClient matidentity,
        MatSmtpClient matsmtp,
        MatDockEngine matdockEngine,
        AppReconciler reconciler,
        AppUrls urls,
        JobService jobs)
    {
        _config = config;
        _apps = apps;
        _catalog = catalog;
        _matdock = matdock;
        _matcad = matcad;
        _matddns = matddns;
        _matidentity = matidentity;
        _matsmtp = matsmtp;
        _matdockEngine = matdockEngine;
        _reconciler = reconciler;
        _urls = urls;
        _jobs = jobs;
    }

    /// <summary>One engine as the page shows it. <see cref="Status"/> is <c>null</c> when it is not installed or its API does not answer.</summary>
    public sealed record EngineRow(CatalogApp Catalog, InstalledApp? App, EngineStatus? Status, string? OpenUrl, string Owns);

    public SystemConfig Config { get; private set; } = new();
    public List<EngineRow> Rows { get; private set; } = new();

    /// <summary>Are the apps deployed through MatDock (else Mattiverse runs them itself)?</summary>
    public bool DeployedByMatDock { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Config = await _config.GetAsync(ct);
        var apps = await _apps.ListAsync(ct);

        var owns = new Dictionary<string, string>
        {
            [EngineIds.MatDock] = "Stacks, containers, backups, schedules and updates of your apps.",
            [EngineIds.Matcad] = "Routes, certificates and logins: the address of every app.",
            [EngineIds.Matddns] = "Sources of your IP, DNS records and the rules that keep them current.",
            [EngineIds.MatIdentity] = "People, groups, and which app each of them may open: one sign-in for all apps.",
            [EngineIds.MatSmtp] = "The relay user of every app that sends mail, the account the mail leaves through, the queue.",
        };

        // One after the other: the clients share the request's database context.
        var clients = new Dictionary<string, EngineClient>
        {
            [EngineIds.MatDock] = _matdock,
            [EngineIds.Matcad] = _matcad,
            [EngineIds.Matddns] = _matddns,
            [EngineIds.MatIdentity] = _matidentity,
            [EngineIds.MatSmtp] = _matsmtp,
        };

        Rows = [];
        foreach (var id in EngineIds.All)
        {
            var catalog = _catalog.Get(id);
            if (catalog is null)
            {
                continue;
            }

            var app = apps.FirstOrDefault(a => a.CatalogId == id && a.Lifecycle != AppLifecycle.Failed)
                ?? apps.FirstOrDefault(a => a.CatalogId == id);
            var installed = app is { Lifecycle: AppLifecycle.Installed };
            Rows.Add(new EngineRow(
                catalog,
                app,
                installed ? await clients[id].GetStatusAsync(ct) : null,
                installed ? await _urls.OpenUrlAsync(app!, Request, ct) : null,
                owns[id]));
        }

        DeployedByMatDock = await _matdockEngine.IsAvailableAsync(ct);
    }

    /// <summary>Gives Matcad and Matddns the domain, the DNS account and the routes and records of all apps again.</summary>
    public async Task<IActionResult> OnPostReconnectAsync()
    {
        var jobId = await _jobs.EnqueueAsync(JobKind.Provision, "Hand the domain and the DNS account to the engines", null, AppJobs.ConnectEngines(), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    /// <summary>Takes the stacks that MatDock holds for Mattiverse and Mattiverse does not know back as apps.</summary>
    public async Task<IActionResult> OnPostAdoptAsync()
    {
        var ct = HttpContext.RequestAborted;
        var count = await _reconciler.ReconcileAsync(_ => { }, ct);
        if (count == 0)
        {
            StatusMessage = "MatDock holds no apps that Mattiverse does not know already.";
            return RedirectToPage();
        }

        // The apps taken over get their route and DNS record again.
        var jobId = await _jobs.EnqueueAsync(JobKind.Provision, $"Connect the {count} app{(count == 1 ? "" : "s")} taken over from MatDock", null, AppJobs.ConnectEngines(), ct);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }
}
