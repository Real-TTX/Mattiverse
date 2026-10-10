using Mattiverse.Core.Catalog;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Apps;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Apps;

/// <summary>
/// The "window" of an app. Like on a phone, exactly one app is open at a time and it fills the screen; the bar on top
/// takes you back. Apps that forbid being embedded are offered in a new tab instead.
/// </summary>
public class OpenModel : PageModel
{
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly AppUrls _urls;
    private readonly FrameProbe _probe;

    public OpenModel(AppService apps, CatalogService catalog, AppUrls urls, FrameProbe probe)
    {
        _apps = apps;
        _catalog = catalog;
        _urls = urls;
        _probe = probe;
    }

    public InstalledApp App { get; private set; } = null!;
    public string? Icon { get; private set; }
    public string? OpenUrl { get; private set; }
    public FrameCheck Frame { get; private set; } = new(false, null);

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var ct = HttpContext.RequestAborted;
        var app = await _apps.GetAsync(id, ct);
        if (app is null)
        {
            return NotFound();
        }

        // An app that is still installing (or failed) has nothing to show yet: go to its page.
        var url = await _urls.OpenUrlAsync(app, Request, ct);
        if (app.Lifecycle != AppLifecycle.Installed || url is null)
        {
            return RedirectToPage("/Apps/Details", new { id });
        }

        App = app;
        Icon = _catalog.IconOf(app.CatalogId);
        OpenUrl = url;
        Frame = await _probe.CheckAsync(app, url, Request.IsHttps, ct);
        return Page();
    }
}
