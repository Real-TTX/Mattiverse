using Mattiverse.Web.Controls;

namespace Mattiverse.Web.Support;

/// <summary>The tab bar shared by the pages of the Settings area.</summary>
public static class SettingsTabs
{
    public static TabBarModel Build(string active) => new()
    {
        Tabs =
        [
            new TabItem { Title = "General", Href = "/Settings", Icon = "settings", Active = active == "general" },
            new TabItem { Title = "Setup", Href = "/Settings/Setup", Icon = "sparkles", Active = active == "setup" },
            new TabItem { Title = "System", Href = "/Settings/System", Icon = "server", Active = active == "system" },
        ],
    };
}
