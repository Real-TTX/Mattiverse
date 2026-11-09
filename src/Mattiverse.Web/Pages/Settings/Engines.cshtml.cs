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
    private readonly EngineOverview _engines;
    private readonly MatDockEngine _matdockEngine;
    private readonly AppReconciler _reconciler;
    private readonly JobService _jobs;
    private readonly FrameLink _frames;

    public EnginesModel(SystemConfigService config, EngineOverview engines, MatDockEngine matdockEngine, AppReconciler reconciler, JobService jobs, FrameLink frames)
    {
        _frames = frames;
        _config = config;
        _engines = engines;
        _matdockEngine = matdockEngine;
        _reconciler = reconciler;
        _jobs = jobs;
    }

    public SystemConfig Config { get; private set; } = new();
    public IReadOnlyList<EngineRow> Rows { get; private set; } = [];

    /// <summary>Are the apps deployed through MatDock (else Mattiverse runs them itself)?</summary>
    public bool DeployedByMatDock { get; private set; }

    /// <summary>The addresses of Mattiverse the engines get as MAT_FRAME_ANCESTORS (with their next update), so the window of the home screen may show them.</summary>
    public IReadOnlyList<string> FrameOrigins { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Config = await _config.GetAsync(ct);
        Rows = await _engines.ListAsync(Request, askApi: true, ct);
        DeployedByMatDock = await _matdockEngine.IsAvailableAsync(ct);
        FrameOrigins = await _frames.OriginsAsync(ct);
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
