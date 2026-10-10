using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Mattiverse.Web.Controls;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Apps;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly DockerService _docker;

    public IndexModel(AppService apps, CatalogService catalog, DockerService docker)
    {
        _apps = apps;
        _catalog = catalog;
        _docker = docker;
    }

    public sealed record Row(InstalledApp App, AppStatusView Status, string? Icon, string Kind);

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string SortKey { get; set; } = "NameAsc";

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public List<Row> Items { get; private set; } = new();
    public PaginationModel Pagination { get; private set; } = new();
    public bool Busy => Items.Any(r => r.App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing);

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var apps = await _apps.ListAsync(ct);
        var runtimes = await _apps.GetRuntimesAsync(ct);

        IEnumerable<Row> rows = apps.Select(a => new Row(
            a,
            AppStatus.Of(a, runtimes.GetValueOrDefault(a.Slug)),
            _catalog.IconOf(a.CatalogId),
            a.Role switch
            {
                AppRoles.Proxy => "Reverse proxy",
                AppRoles.Ddns => "DynDNS client",
                AppRoles.Docker => "Docker management",
                _ => _catalog.Get(a.CatalogId)?.Category ?? "App",
            }));

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            rows = rows.Where(r => r.App.Name.Contains(s, Ic) || r.App.Slug.Contains(s, Ic) || r.Kind.Contains(s, Ic));
        }

        if (!string.IsNullOrWhiteSpace(Status))
        {
            rows = Status switch
            {
                "running" => rows.Where(r => r.Status.Css == "online"),
                "stopped" => rows.Where(r => r.Status.Css == "unknown"),
                "attention" => rows.Where(r => r.Status.Css is "offline" or "warn"),
                _ => rows,
            };
        }

        rows = SortKey switch
        {
            "NameDesc" => rows.OrderByDescending(r => r.App.Name, StringComparer.OrdinalIgnoreCase),
            "Newest" => rows.OrderByDescending(r => r.App.CreateDate),
            "Status" => rows.OrderBy(r => r.Status.Css).ThenBy(r => r.App.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.App.Name, StringComparer.OrdinalIgnoreCase),
        };

        var list = rows.ToList();
        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = list.Count };
        Pagination.Normalize();
        Items = list.Skip((Pagination.Page - 1) * PageSize).Take(PageSize).ToList();
    }
}
