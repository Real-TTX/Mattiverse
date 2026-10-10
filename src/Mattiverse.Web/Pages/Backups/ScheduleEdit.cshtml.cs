using System.ComponentModel.DataAnnotations;
using Mattiverse.Core.Apps;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages.Backups;

public class ScheduleEditModel : PageModel
{
    private readonly MattiverseDbContext _db;
    private readonly AppService _apps;
    private readonly SystemConfigService _config;

    public ScheduleEditModel(MattiverseDbContext db, AppService apps, SystemConfigService config)
    {
        _db = db;
        _apps = apps;
        _config = config;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public List<InstalledApp> Apps { get; private set; } = new();

    public bool IsEdit => Input.Id is > 0;

    public class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(200)]
        public string Name { get; set; } = "Nightly backup";

        public long? AppId { get; set; }

        public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

        [Required(ErrorMessage = "Please choose a time.")]
        public string Time { get; set; } = "03:00";

        public DayOfWeek Weekday { get; set; } = DayOfWeek.Sunday;

        [Range(1, 365, ErrorMessage = "Keep between 1 and 365 backups.")]
        public int Retention { get; set; } = 7;

        public bool StopContainers { get; set; } = true;

        public bool Enabled { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        await LoadAppsAsync();
        if (id is > 0)
        {
            var schedule = await _db.BackupSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, HttpContext.RequestAborted);
            if (schedule is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = schedule.Id,
                Name = schedule.Name,
                AppId = schedule.AppId,
                Frequency = schedule.Frequency,
                Time = TimeSpan.FromMinutes(schedule.MinuteOfDay).ToString(@"hh\:mm"),
                Weekday = schedule.Weekday,
                Retention = schedule.Retention,
                StopContainers = schedule.StopContainers,
                Enabled = schedule.Enabled,
            };
        }
        else
        {
            var defaults = (await _config.GetAsync(HttpContext.RequestAborted)).Backups;
            Input.Retention = defaults.Retention;
            Input.StopContainers = defaults.StopContainers;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAppsAsync();
        if (!TimeSpan.TryParse(Input.Time, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            ModelState.AddModelError("Input.Time", "Please enter a time like 03:00.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        BackupSchedule schedule;
        if (IsEdit)
        {
            var existing = await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == Input.Id, HttpContext.RequestAborted);
            if (existing is null)
            {
                return NotFound();
            }

            schedule = existing;
        }
        else
        {
            schedule = new BackupSchedule();
            _db.BackupSchedules.Add(schedule);
        }

        schedule.Name = Input.Name.Trim();
        schedule.AppId = Input.AppId;
        schedule.Frequency = Input.Frequency;
        schedule.MinuteOfDay = (int)time.TotalMinutes;
        schedule.Weekday = Input.Weekday;
        schedule.Retention = Input.Retention;
        schedule.StopContainers = Input.StopContainers;
        schedule.Enabled = Input.Enabled;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        return RedirectToPage("/Backups/Index", new { tab = "schedules" });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var schedule = await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == Input.Id, HttpContext.RequestAborted);
        if (schedule is not null)
        {
            _db.BackupSchedules.Remove(schedule);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
        }

        return RedirectToPage("/Backups/Index", new { tab = "schedules" });
    }

    private async Task LoadAppsAsync()
        => Apps = (await _apps.ListAsync(HttpContext.RequestAborted)).Where(a => a.Lifecycle == AppLifecycle.Installed).ToList();
}
