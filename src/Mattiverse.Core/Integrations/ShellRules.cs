using Mattiverse.Core.Entities;

namespace Mattiverse.Core.Integrations;

/// <summary>The rules of the home screen for what the apps contribute (MatDesign docs/PLATFORM.md "Contributions"): who sees it and which addresses it may open.</summary>
public static class ShellRules
{
    /// <summary>
    /// Who sees what: everyone what is for everyone, an administrator also what is for administrators. A launcher for the <c>person</c> is for everyone (it opens the
    /// app as that person anyway); a widget with the values of the person is not shown yet (Mattiverse cannot ask an app on a person's behalf).
    /// </summary>
    public static bool Sees(string? audience, bool isAdmin, bool launcher) => audience switch
    {
        null or "everyone" => true,
        "admin" => isAdmin,
        "person" => launcher,
        _ => false,
    };

    /// <summary>An address of the app: one slash first, no second slash, no backslash and no other site anywhere in it.</summary>
    public static bool IsPath(string? path)
        => path is { Length: > 0 } && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains("://", StringComparison.Ordinal);

    /// <summary>The window of Mattiverse at an address of the app.</summary>
    public static string WindowOf(InstalledApp app, string path) => $"/Apps/Open?id={app.Id}&path={Uri.EscapeDataString(path)}";
}
