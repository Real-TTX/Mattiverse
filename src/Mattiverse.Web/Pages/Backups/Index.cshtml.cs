using Mattiverse.Core.Backups;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Mattiverse.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages.Backups;

public class IndexModel : PageModel
{
    private const int PageSize = 15;

    private readonly MattiverseDbContext _db;
    private readonly BackupHub _backups;
    private readonly CatalogService _catalog;

    public IndexModel(MattiverseDbContext db, BackupHub backups, CatalogService catalog)
    {
        _db = db;
        _backups = backups;
        _catalog = catalog;
    }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? App { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string SortKey { get; set; } = "NewestFirst";

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public bool SchedulesTab => Tab == "schedules";
    public List<BackupItem> Items { get; private set; } = new();
    public List<ScheduleItem> Schedules { get; private set; } = new();
    public Dictionary<string, string> AppNames { get; private set; } = new();
    public List<string> AppSlugs { get; private set; } = new();
    public PaginationModel Pagination { get; private set; } = new();
    public long TotalSize { get; private set; }

    /// <summary>MatDock runs apps but does not answer: its backups and schedules cannot be listed right now.</summary>
    public bool MatDockSilent { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public TabBarModel Tabs => new()
    {
        Tabs =
        [
            new TabItem { Title = "History", Href = "/Backups", Icon = "clock", Active = !SchedulesTab },
            new TabItem { Title = "Schedules", Href = "/Backups?tab=schedules", Icon = "calendar", Active = SchedulesTab },
        ],
    };

    public string? IconOf(string catalogId) => _catalog.IconOf(catalogId);

    /// <summary>Where a backup or schedule is kept, as the label the lists show.</summary>
    public static string EngineLabel(string engine) => engine == "matdock" ? "MatDock" : "Mattiverse";

    public string AppNameOf(string? slug) => slug is null ? "All apps" : AppNames.GetValueOrDefault(slug, "(removed)");

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        AppNames = await _db.InstalledApps.AsNoTracking().ToDictionaryAsync(a => a.Slug, a => a.Name, ct);
        MatDockSilent = await _db.InstalledApps.AnyAsync(a => a.Engine == "matdock", ct) && !await _backups.ForEngine("matdock").IsAvailableAsync(ct);

        if (SchedulesTab)
        {
            Schedules = await _backups.ListSchedulesAsync(ct);
            return;
        }

        var all = await _backups.ListAsync(ct);
        AppSlugs = all.Select(b => b.AppSlug).Distinct().Order().ToList();

        IEnumerable<BackupItem> query = all;
        if (!string.IsNullOrWhiteSpace(App))
        {
            query = query.Where(b => b.AppSlug == App);
        }

        if (Enum.TryParse<BackupKind>(Kind, out var kind))
        {
            query = query.Where(b => b.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            query = query.Where(b => b.AppName.Contains(s, StringComparison.OrdinalIgnoreCase) || (b.Note?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = query.ToList();
        TotalSize = filtered.Where(b => b.Status == BackupStatus.Succeeded).Sum(b => b.SizeBytes);

        filtered = SortKey switch
        {
            "OldestFirst" => filtered.OrderBy(b => b.CreatedUtc).ToList(),
            "Largest" => filtered.OrderByDescending(b => b.SizeBytes).ToList(),
            "App" => filtered.OrderBy(b => b.AppName, StringComparer.OrdinalIgnoreCase).ThenByDescending(b => b.CreatedUtc).ToList(),
            _ => filtered.OrderByDescending(b => b.CreatedUtc).ToList(),
        };

        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = filtered.Count };
        Pagination.Normalize();
        Items = filtered.Skip((Pagination.Page - 1) * PageSize).Take(PageSize).ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        await _backups.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "The backup was deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteScheduleAsync(string id)
    {
        await _backups.DeleteScheduleAsync(id, HttpContext.RequestAborted);
        StatusMessage = "The schedule was deleted. Existing backups are kept.";
        return RedirectToPage(new { tab = "schedules" });
    }

    /// <summary>Streams a backup as a tar file, so it can be copied off the host.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(string id)
    {
        var download = await _backups.OpenDownloadAsync(id, HttpContext.RequestAborted);
        if (download is null)
        {
            return NotFound();
        }

        Response.ContentType = "application/x-tar";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{download.FileName}\"";
        await download.WriteToAsync(Response.Body, HttpContext.RequestAborted);
        return new EmptyResult();
    }
}
