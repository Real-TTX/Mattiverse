using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Mattiverse.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages.Jobs;

public class IndexModel : PageModel
{
    private const int PageSize = 15;

    private readonly MattiverseDbContext _db;

    public IndexModel(MattiverseDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string SortKey { get; set; } = "NewestFirst";

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public List<Job> Items { get; private set; } = new();
    public PaginationModel Pagination { get; private set; } = new();
    public bool Busy => Items.Any(j => j.Status is JobStatus.Queued or JobStatus.Running);

    public async Task OnGetAsync()
    {
        var query = _db.Jobs.AsNoTracking().AsQueryable();

        if (Enum.TryParse<JobStatus>(Status, out var status))
        {
            query = query.Where(j => j.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            query = query.Where(j => EF.Functions.Like(j.Title, $"%{s}%") || (j.AppSlug != null && EF.Functions.Like(j.AppSlug, $"%{s}%")));
        }

        query = SortKey == "OldestFirst" ? query.OrderBy(j => j.Id) : query.OrderByDescending(j => j.Id);

        var total = await query.CountAsync(HttpContext.RequestAborted);
        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = total };
        Pagination.Normalize();
        Items = await query.Skip((Pagination.Page - 1) * PageSize).Take(PageSize).ToListAsync(HttpContext.RequestAborted);
    }
}
