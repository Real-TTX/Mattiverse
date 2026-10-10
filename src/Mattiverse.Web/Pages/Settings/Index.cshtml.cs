using System.ComponentModel.DataAnnotations;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Integrations;
using Mattiverse.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Settings;

public class IndexModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly JobService _jobs;
    private readonly ProxyPublisher _proxy;

    public IndexModel(SystemConfigService config, JobService jobs, ProxyPublisher proxy)
    {
        _config = config;
        _jobs = jobs;
        _proxy = proxy;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData] public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(60)]
        public string InstanceName { get; set; } = "Mattiverse";

        [StringLength(253)]
        [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a host name or IP address like nas.local.")]
        public string? PublicHost { get; set; }

        [StringLength(253)]
        [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a domain like example.com.")]
        public string? BaseDomain { get; set; }

        [Range(1, 365, ErrorMessage = "Keep between 1 and 365 backups.")]
        public int BackupRetention { get; set; } = 7;

        public bool BackupStopContainers { get; set; } = true;
    }

    public async Task OnGetAsync()
    {
        var config = await _config.GetAsync(HttpContext.RequestAborted);
        Input = new InputModel
        {
            InstanceName = config.InstanceName,
            PublicHost = config.PublicHost,
            BaseDomain = config.BaseDomain,
            BackupRetention = config.Backups.Retention,
            BackupStopContainers = config.Backups.StopContainers,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var ct = HttpContext.RequestAborted;
        var before = await _config.GetAsync(ct);
        var baseDomain = string.IsNullOrWhiteSpace(Input.BaseDomain) ? null : Input.BaseDomain.Trim().ToLowerInvariant();

        await _config.UpdateAsync(c =>
        {
            c.InstanceName = Input.InstanceName.Trim();
            c.PublicHost = string.IsNullOrWhiteSpace(Input.PublicHost) ? null : Input.PublicHost.Trim();
            c.BaseDomain = baseDomain;
            c.Backups.Retention = Input.BackupRetention;
            c.Backups.StopContainers = Input.BackupStopContainers;
        }, ct);

        // A new base domain means new host names: let the reverse proxy know.
        if (!string.Equals(before.BaseDomain, baseDomain, StringComparison.OrdinalIgnoreCase) && baseDomain is not null && await _proxy.IsActiveAsync(ct))
        {
            var jobId = await _jobs.EnqueueAsync(JobKind.Provision, "Update the routes of the reverse proxy", null, AppJobs.PublishRoutes(), ct);
            return RedirectToPage("/Jobs/Details", new { id = jobId });
        }

        StatusMessage = "Settings saved.";
        return RedirectToPage();
    }
}
