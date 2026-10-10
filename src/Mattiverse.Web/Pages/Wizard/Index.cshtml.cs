using System.ComponentModel.DataAnnotations;
using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Integrations;
using Mattiverse.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Wizard;

/// <summary>
/// The setup wizard (first start, and again whenever the user wants): which reverse proxy, which DynDNS client, which advanced
/// Docker management. It installs what is chosen and leaves alone what is already there.
/// </summary>
public class IndexModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly CatalogService _catalog;
    private readonly AppService _apps;
    private readonly DockerService _docker;
    private readonly PortPlanner _ports;
    private readonly JobService _jobs;
    private readonly ProxyPublisher _proxy;

    public IndexModel(
        SystemConfigService config,
        CatalogService catalog,
        AppService apps,
        DockerService docker,
        PortPlanner ports,
        JobService jobs,
        ProxyPublisher proxy)
    {
        _config = config;
        _catalog = catalog;
        _apps = apps;
        _docker = docker;
        _ports = ports;
        _jobs = jobs;
        _proxy = proxy;
    }

    /// <summary>One question of the wizard with its choices.</summary>
    public sealed record Question(string Field, string Role, string Title, string Description, string Icon, IReadOnlyList<Choice> Choices, string? Selected);

    /// <summary>An answer: a catalog app (possibly installed already), with warnings about ports that are taken.</summary>
    public sealed record Choice(CatalogApp App, InstalledApp? Installed, IReadOnlyList<PortConflict> Conflicts);

    [BindProperty]
    [StringLength(253)]
    [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a host name or IP address like nas.local.")]
    public string? PublicHost { get; set; }

    [BindProperty]
    [StringLength(253)]
    [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a domain like example.com.")]
    public string? BaseDomain { get; set; }

    [BindProperty] public string? ProxyAppId { get; set; }
    [BindProperty] public string? DdnsAppId { get; set; }
    [BindProperty] public string? DockerAppId { get; set; }

    public DockerEngineInfo Engine { get; private set; } = new(false, null, string.Empty, string.Empty, 0, 0, 0, 0, 0);
    public List<Question> Questions { get; private set; } = new();
    public bool AlreadyCompleted { get; private set; }

    public async Task OnGetAsync()
    {
        var config = await _config.GetAsync(HttpContext.RequestAborted);
        AlreadyCompleted = config.WizardCompleted;
        PublicHost = config.PublicHost ?? Request.Host.Host;
        BaseDomain = config.BaseDomain;
        await LoadAsync(config, useSaved: true);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        if (!ModelState.IsValid)
        {
            await LoadAsync(config, useSaved: false);
            return Page();
        }

        var baseDomain = string.IsNullOrWhiteSpace(BaseDomain) ? null : BaseDomain.Trim().ToLowerInvariant();
        var domainChanged = !string.Equals(config.BaseDomain, baseDomain, StringComparison.OrdinalIgnoreCase);

        await _config.UpdateAsync(c =>
        {
            c.PublicHost = string.IsNullOrWhiteSpace(PublicHost) ? null : PublicHost.Trim();
            c.BaseDomain = baseDomain;
            c.Selection = new WizardSelection
            {
                ProxyAppId = Clean(ProxyAppId),
                DdnsAppId = Clean(DdnsAppId),
                DockerAppId = Clean(DockerAppId),
            };
            c.WizardCompleted = true;
            c.WizardCompletedAt = DateTime.UtcNow;
        }, ct);

        // Install in the order that makes sense: the proxy first, so the apps after it get their routes right away.
        var installed = await _apps.ListAsync(ct);
        var jobIds = new List<long>();
        foreach (var catalogId in new[] { Clean(ProxyAppId), Clean(DdnsAppId), Clean(DockerAppId) })
        {
            if (catalogId is null || installed.Any(a => a.CatalogId == catalogId && a.Lifecycle != AppLifecycle.Failed))
            {
                continue;
            }

            var result = await _apps.PrepareInstallAsync(new InstallRequest(catalogId, null, null, new Dictionary<string, string?>()), ct);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, $"{catalogId}: {string.Join(' ', result.Errors)}");
                await LoadAsync(await _config.GetAsync(ct), useSaved: false);
                return Page();
            }

            var app = result.App!;
            jobIds.Add(await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name}", app, AppJobs.Install(app.Id), ct));
        }

        // The proxy was there before and the domain is new: the routes need to follow.
        if (jobIds.Count == 0 && domainChanged && baseDomain is not null && await _proxy.IsActiveAsync(ct))
        {
            jobIds.Add(await _jobs.EnqueueAsync(JobKind.Provision, "Update the routes of the reverse proxy", null, AppJobs.PublishRoutes(), ct));
        }

        return jobIds.Count == 0
            ? RedirectToPage("/Index")
            : RedirectToPage("/Wizard/Progress", new { jobs = string.Join(',', jobIds) });
    }

    private async Task LoadAsync(SystemConfig config, bool useSaved)
    {
        var ct = HttpContext.RequestAborted;
        Engine = await _docker.GetEngineInfoAsync(ct);
        var installed = await _apps.ListAsync(ct);

        async Task<Question> MakeAsync(string field, string role, string title, string description, string icon, string? saved, string? current)
        {
            var choices = new List<Choice>();
            foreach (var app in _catalog.GetByRole(role))
            {
                var existing = installed.FirstOrDefault(a => a.CatalogId == app.Id);
                var defaults = app.Variables.Where(v => v.Default is not null).ToDictionary(v => v.Key, v => v.Default!);
                var conflicts = existing is null ? await _ports.FindConflictsAsync(app, defaults, "\0", ct) : [];
                choices.Add(new Choice(app, existing, conflicts));
            }

            // What is installed wins; then what the user chose last time; a new setup starts with the recommended app.
            var selected = useSaved
                ? choices.FirstOrDefault(c => c.Installed is not null)?.App.Id
                    ?? (config.WizardCompleted ? saved : choices.FirstOrDefault(c => c.App.Recommended)?.App.Id)
                : current;
            return new Question(field, role, title, description, icon, choices, selected);
        }

        Questions =
        [
            await MakeAsync(nameof(ProxyAppId), AppRoles.Proxy, "Reverse proxy", "A reverse proxy gives each app its own address like mail.example.com and takes care of HTTPS.", "route", config.Selection.ProxyAppId, ProxyAppId),
            await MakeAsync(nameof(DdnsAppId), AppRoles.Ddns, "DynDNS client", "Your home IP changes now and then. A DynDNS client keeps your domain pointing at it.", "globe", config.Selection.DdnsAppId, DdnsAppId),
            await MakeAsync(nameof(DockerAppId), AppRoles.Docker, "Docker management", "Mattiverse installs, updates and backs up apps. For everything else, a full Docker manager helps.", "whale", config.Selection.DockerAppId, DockerAppId),
        ];
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
