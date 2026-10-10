using System.Reflection;

namespace Mattiverse.Core;

/// <summary>The version of the running Mattiverse (the assembly's informational version, set by the build per channel).</summary>
public static class AppVersion
{
    public static string Current { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
}
