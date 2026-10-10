using Mattiverse.Core.Catalog;
using Mattiverse.Core.Data;
using Mattiverse.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages.Store;

public class IndexModel : PageModel
{
    private const int PageSize = 12;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly CatalogService _catalog;
    private readonly MattiverseDbContext _db;

    public IndexModel(CatalogService catalog, MattiverseDbContext db)
    {
        _catalog = catalog;
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string SortKey { get; set; } = "NameAsc";

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public List<CatalogApp> Items { get; private set; } = new();
    public List<string> Categories { get; private set; } = new();
    public Dictionary<string, int> InstalledCounts { get; private set; } = new();
    public PaginationModel Pagination { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        var all = _catalog.GetAll();
        Categories = all.Select(a => a.Category).Distinct().Order().ToList();
        InstalledCounts = (await _db.InstalledApps.GroupBy(a => a.CatalogId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(HttpContext.RequestAborted))
            .ToDictionary(x => x.Key, x => x.Count);

        IEnumerable<CatalogApp> query = all;
        if (!string.IsNullOrWhiteSpace(Category))
        {
            query = query.Where(a => a.Category.Equals(Category, Ic));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            query = query.Where(a => a.Name.Contains(s, Ic) || a.Tagline.Contains(s, Ic) || a.Description.Contains(s, Ic) || a.Category.Contains(s, Ic));
        }

        query = SortKey switch
        {
            "NameDesc" => query.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase),
            "Category" => query.OrderBy(a => a.Category, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
        };

        var list = query.ToList();
        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = list.Count };
        Pagination.Normalize();
        Items = list.Skip((Pagination.Page - 1) * PageSize).Take(PageSize).ToList();
    }

    /// <summary>Picks up catalog files that were added to <c>/data/catalog</c> without a restart.</summary>
    public IActionResult OnPostReload()
    {
        _catalog.Reload();
        StatusMessage = $"Catalog reloaded: {_catalog.GetAll().Count} apps.";
        return RedirectToPage();
    }
}
