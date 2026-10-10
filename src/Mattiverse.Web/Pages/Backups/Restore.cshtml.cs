using Mattiverse.Core.Apps;
using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages.Backups;

public class RestoreModel : PageModel
{
    private readonly MattiverseDbContext _db;
    private readonly AppService _apps;
    private readonly JobService _jobs;

    public RestoreModel(MattiverseDbContext db, AppService apps, JobService jobs)
    {
        _db = db;
        _apps = apps;
        _jobs = jobs;
    }

    public Backup Backup { get; private set; } = null!;

    /// <summary>The app as it exists now; <c>null</c> when it was uninstalled (the restore re-creates it).</summary>
    public InstalledApp? App { get; private set; }

    [BindProperty]
    public bool BackupFirst { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(long id)
        => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(long id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var jobId = await _jobs.EnqueueAsync(
            JobKind.Restore,
            $"Restore {Backup.AppName} from {Stamp(Backup)}",
            App,
            AppJobs.Restore(Backup.Id, App is not null && BackupFirst),
            HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task<bool> LoadAsync(long id)
    {
        var backup = await _db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.Status == BackupStatus.Succeeded, HttpContext.RequestAborted);
        if (backup is null)
        {
            return false;
        }

        Backup = backup;
        App = await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == backup.AppSlug, HttpContext.RequestAborted);
        return true;
    }

    private static string Stamp(Backup backup) => backup.CreateDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
