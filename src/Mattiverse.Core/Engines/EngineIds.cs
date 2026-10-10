namespace Mattiverse.Core.Engines;

/// <summary>The catalog ids of the three engines that are part of Mattiverse (see docs/ENGINES.md).</summary>
public static class EngineIds
{
    public const string MatDock = "matdock";
    public const string Matcad = "matcad";
    public const string Matddns = "matddns";

    /// <summary>In the order they are installed: MatDock first, because it deploys the others.</summary>
    public static readonly IReadOnlyList<string> All = [MatDock, Matcad, Matddns];

    public static bool IsEngine(string? catalogId) => All.Contains(catalogId ?? string.Empty);
}
