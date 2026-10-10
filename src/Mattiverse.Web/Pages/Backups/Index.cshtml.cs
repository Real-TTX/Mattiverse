using System.Formats.Tar;
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
    private readonly BackupService _backups;
    private readonly CatalogService _catalog;

    public IndexModel(MattiverseDbContext db, BackupService backups, CatalogService catalog)
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
    public List<Backup> Items { get; private set; } = new();
    public List<BackupSchedule> Schedules { get; private set; } = new();
    public Dictionary<long, string> AppNames { get; private set; } = new();
    public List<string> AppSlugs { get; private set; } = new();
    public PaginationModel Pagination { get; private set; } = new();
    public long TotalSize { get; private set; }

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

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        AppNames = await _db.InstalledApps.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        if (SchedulesTab)
        {
            Schedules = await _db.BackupSchedules.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
            return;
        }

        AppSlugs = await _db.Backups.AsNoTracking().Select(b => b.AppSlug).Distinct().OrderBy(s => s).ToListAsync(ct);
        var query = _db.Backups.AsNoTracking().AsQueryable();

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
            query = query.Where(b => EF.Functions.Like(b.AppName, $"%{s}%") || (b.Note != null && EF.Functions.Like(b.Note, $"%{s}%")));
        }

        TotalSize = await query.Where(b => b.Status == BackupStatus.Succeeded).SumAsync(b => (long?)b.SizeBytes, ct) ?? 0;

        query = SortKey switch
        {
            "OldestFirst" => query.OrderBy(b => b.Id),
            "Largest" => query.OrderByDescending(b => b.SizeBytes),
            "App" => query.OrderBy(b => b.AppName).ThenByDescending(b => b.Id),
            _ => query.OrderByDescending(b => b.Id),
        };

        var total = await query.CountAsync(ct);
        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = total };
        Pagination.Normalize();
        Items = await query.Skip((Pagination.Page - 1) * PageSize).Take(PageSize).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await _backups.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "The backup was deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteScheduleAsync(long id)
    {
        var schedule = await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == id, HttpContext.RequestAborted);
        if (schedule is not null)
        {
            _db.BackupSchedules.Remove(schedule);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            StatusMessage = "The schedule was deleted. Existing backups are kept.";
        }

        return RedirectToPage(new { tab = "schedules" });
    }

    /// <summary>Streams a backup as a tar file, so it can be copied off the host.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(long id)
    {
        var backup = await _db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.Status == BackupStatus.Succeeded, HttpContext.RequestAborted);
        var folder = backup is null ? null : _backups.FolderOf(backup);
        if (backup is null || folder is null || !Directory.Exists(folder))
        {
            return NotFound();
        }

        Response.ContentType = "application/x-tar";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{backup.AppSlug}-{backup.Folder}.tar\"";

        await using (var writer = new TarWriter(Response.Body, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var entryName = $"{backup.AppSlug}-{backup.Folder}/{Path.GetRelativePath(folder, file).Replace('\\', '/')}";
                await writer.WriteEntryAsync(file, entryName, HttpContext.RequestAborted);
            }
        }

        return new EmptyResult();
    }
}
