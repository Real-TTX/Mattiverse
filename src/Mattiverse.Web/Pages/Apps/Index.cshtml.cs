using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Entities;
using Mattiverse.Web.Controls;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Apps;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["name", "status", "added"];

    private readonly AppService _apps;
    private readonly CatalogService _catalog;

    public IndexModel(AppService apps, CatalogService catalog)
    {
        _apps = apps;
        _catalog = catalog;
    }

    public sealed record Row(InstalledApp App, AppStatusView Status, string? Icon, string Kind);

    public ListView<Row> List { get; private set; } = default!;

    public bool Busy => List.Items.Any(r => r.App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing);

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var apps = await _apps.ListAsync(ct);
        var runtimes = await _apps.GetRuntimesAsync(ct);

        var all = apps.Select(a => new Row(
            a,
            AppStatus.Of(a, runtimes.GetValueOrDefault(a.Slug)),
            _catalog.IconOf(a.CatalogId),
            a.Role switch
            {
                AppRoles.Proxy => "Reverse proxy",
                AppRoles.Ddns => "DynDNS client",
                AppRoles.Docker => "Docker management",
                AppRoles.Identity => "Single sign-on",
                _ => _catalog.Get(a.CatalogId)?.Category ?? "App",
            })).ToList();

        var q = ListQuery.Text(Request, "q");
        var status = ListQuery.Text(Request, "status");
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<Row> rows = all;
        if (q is not null)
        {
            rows = rows.Where(r => r.App.Name.Contains(q, Ic) || r.App.Slug.Contains(q, Ic) || r.Kind.Contains(q, Ic));
        }

        rows = status switch
        {
            "running" => rows.Where(r => r.Status.Css == "online"),
            "stopped" => rows.Where(r => r.Status.Css == "unknown"),
            "attention" => rows.Where(r => r.Status.Css is "offline" or "warn"),
            _ => rows,
        };

        Func<Row, object> key = sort switch
        {
            "status" => r => r.Status.Css,
            "added" => r => r.App.CreateDate,
            _ => r => r.App.Name.ToLowerInvariant(),
        };
        var list = (descending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ThenBy(r => r.App.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "apps",
            Action = "/Apps",
            Label = "Apps",
            Search = q,
            SearchPlaceholder = "Search apps",
            SearchField = true,
            Filters =
            [
                new FilterField("status", "Status", [new("running", "Running"), new("stopped", "Stopped"), new("attention", "Needs attention")], status),
            ],
            SortOptions = [new("name", "Name"), new("status", "Status"), new("added", "Added")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "app" : "apps",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<Row> { Items = items, Toolbar = toolbar, Pager = pager };
    }
}
