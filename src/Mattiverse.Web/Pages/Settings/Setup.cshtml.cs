using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Entities;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Settings;

/// <summary>Shows the three infrastructure choices of the setup wizard and what is installed for each.</summary>
public class SetupModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly AppService _apps;
    private readonly CatalogService _catalog;

    public SetupModel(SystemConfigService config, AppService apps, CatalogService catalog)
    {
        _config = config;
        _apps = apps;
        _catalog = catalog;
    }

    public sealed record Slot(string Role, string Title, string Description, string Icon, InstalledApp? Installed, string? IconFile);

    public SystemConfig Config { get; private set; } = new();
    public List<Slot> Slots { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Config = await _config.GetAsync(ct);
        var apps = await _apps.ListAsync(ct);

        Slot Make(string role, string title, string description, string icon)
        {
            var installed = apps.FirstOrDefault(a => a.Role == role && a.Lifecycle == AppLifecycle.Installed)
                ?? apps.FirstOrDefault(a => a.Role == role);
            return new Slot(role, title, description, icon, installed, installed is null ? null : _catalog.IconOf(installed.CatalogId));
        }

        Slots =
        [
            Make(AppRoles.Proxy, "Reverse proxy", "Gives your apps a domain and HTTPS.", "route"),
            Make(AppRoles.Ddns, "DynDNS client", "Keeps your domain pointing at your home IP.", "globe"),
            Make(AppRoles.Docker, "Docker management", "For everything beyond installing and backing up apps.", "whale"),
        ];
    }
}
