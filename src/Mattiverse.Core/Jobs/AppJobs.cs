using Mattiverse.Core.Apps;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mattiverse.Core.Jobs;

/// <summary>The work of each job kind, so the pages only say <em>what</em> to run and the worker runs it.</summary>
public static class AppJobs
{
    public static Func<JobContext, Task> Install(long appId)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunInstallAsync(appId, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Update(long appId, bool backupFirst)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunUpdateAsync(appId, backupFirst, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Uninstall(long appId, bool deleteData, bool backupFirst)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunUninstallAsync(appId, deleteData, backupFirst, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Lifecycle(long appId, string command)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunLifecycleAsync(appId, command, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Backup(long appId, BackupKind kind, bool stopContainers, string? note)
        => async ctx =>
        {
            var app = await ctx.Services.GetRequiredService<AppService>().GetAsync(appId, ctx.Cancellation)
                ?? throw new InvalidOperationException("The app does not exist (any more).");
            await ctx.Services.GetRequiredService<BackupService>().RunBackupAsync(app, kind, stopContainers, note, ctx.Log, ctx.Cancellation);
        };

    public static Func<JobContext, Task> Restore(long backupId, bool backupCurrentFirst)
        => ctx => ctx.Services.GetRequiredService<BackupService>().RestoreAsync(backupId, backupCurrentFirst, ctx.Log, ctx.Cancellation);

    /// <summary>A run of a backup schedule for one app: backup, retention, and the result on the schedule.</summary>
    public static Func<JobContext, Task> ScheduledBackup(long appId, long scheduleId)
        => async ctx =>
        {
            var services = ctx.Services;
            var db = services.GetRequiredService<MattiverseDbContext>();
            var schedule = await db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ctx.Cancellation)
                ?? throw new InvalidOperationException("The schedule does not exist (any more).");
            var app = await services.GetRequiredService<AppService>().GetAsync(appId, ctx.Cancellation)
                ?? throw new InvalidOperationException("The app does not exist (any more).");

            try
            {
                var backups = services.GetRequiredService<BackupService>();
                await backups.RunBackupAsync(app, BackupKind.Scheduled, schedule.StopContainers, $"Schedule '{schedule.Name}'", ctx.Log, ctx.Cancellation);
                await backups.ApplyRetentionAsync(app.Slug, schedule.Retention, ctx.Log, ctx.Cancellation);
                schedule.LastStatus = $"{DateTime.Now:g}: {app.Name} backed up.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                schedule.LastStatus = $"{DateTime.Now:g}: {app.Name} failed: {ex.Message}";
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            await db.SaveChangesAsync(ctx.Cancellation);
        };
}
