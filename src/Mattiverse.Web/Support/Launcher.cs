namespace Mattiverse.Web.Support;

/// <summary>A part of Mattiverse shown as an app on the home screen: the store, the backups, the settings ... They open like the pages they are.</summary>
/// <param name="Name">The name under the icon (and the tooltip in the dock).</param>
/// <param name="Href">The page it opens.</param>
/// <param name="Icon">An icon of the toolkit's sprite.</param>
/// <param name="Look">The colour of its tile: <c>mv-appicon--accent</c>, <c>mv-appicon--grey</c> or one of the hues <c>mv-hue-0</c> to <c>mv-hue-11</c>.</param>
/// <param name="Count">A number on the corner of the icon (running tasks), or <c>null</c>.</param>
/// <param name="CountLabel">What the number means, for a screen reader.</param>
public sealed record SystemApp(string Name, string Href, string Icon, string Look, int? Count = null, string? CountLabel = null);

/// <summary>Which parts of Mattiverse the home screen shows as apps, for an administrator and for a user (the menus of <see cref="Navigation"/> as icons).</summary>
public static class Launcher
{
    /// <summary>The dock at the bottom: what is used most.</summary>
    public static IReadOnlyList<SystemApp> Dock(bool isAdmin) => isAdmin
        ?
        [
            new("Store", "/Store", "store", "mv-appicon--accent"),
            new("Apps", "/Apps", "apps", "mv-hue-2"),
            new("Backups", "/Backups", "save", "mv-hue-10"),
            new("Settings", "/Settings", "settings", "mv-appicon--grey"),
        ]
        :
        [
            new("Apps", "/Apps", "apps", "mv-hue-2"),
            new("Account", "/Account/Profile", "user", "mv-appicon--grey"),
        ];

    /// <summary>The others, as icons on the screen after the installed apps.</summary>
    public static IReadOnlyList<SystemApp> OnScreen(bool isAdmin, int activeJobs) => isAdmin
        ?
        [
            new("Activity", "/Jobs", "activity", "mv-hue-4", activeJobs > 0 ? activeJobs : null, activeJobs == 1 ? "task running" : "tasks running"),
            new("Users", "/Users", "users", "mv-hue-8"),
        ]
        : [];
}
