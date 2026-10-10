using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;

namespace Mattiverse.Web.Support;

/// <summary>How an app is shown as a pill and as the dot on a tile: one place decides the words and colours.</summary>
public sealed record AppStatusView(string Css, string Text);

public static class AppStatus
{
    public static AppStatusView Of(InstalledApp app, RuntimeInfo? runtime)
    {
        switch (app.Lifecycle)
        {
            case AppLifecycle.Installing:
                return new AppStatusView("busy", "Installing");
            case AppLifecycle.Removing:
                return new AppStatusView("busy", "Removing");
            case AppLifecycle.Failed:
                return new AppStatusView("offline", "Failed");
        }

        return runtime?.State switch
        {
            RuntimeState.Running => new AppStatusView("online", "Running"),
            RuntimeState.Partial => new AppStatusView("warn", runtime.Summary),
            RuntimeState.Unhealthy => new AppStatusView("offline", "Needs attention"),
            RuntimeState.Stopped => new AppStatusView("unknown", "Stopped"),
            RuntimeState.Missing => new AppStatusView("unknown", "Not created"),
            _ => new AppStatusView("unknown", "Unknown"),
        };
    }

    /// <summary>The tile dot uses the same colour names as the pill.</summary>
    public static string Dot(InstalledApp app, RuntimeInfo? runtime) => Of(app, runtime).Css;
}
