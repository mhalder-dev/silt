namespace Silt.Core.Attribution;

/// <summary>What kind of storage a location represents.</summary>
public enum AppLocationKind
{
    /// <summary>Program Files, or a per-user install under Local\Programs.</summary>
    Install,

    /// <summary>%LOCALAPPDATA%\&lt;app&gt; — machine-specific state, usually the big one.</summary>
    LocalData,

    /// <summary>%APPDATA%\&lt;app&gt; — settings intended to roam with the profile.</summary>
    RoamingData,

    /// <summary>%LOCALAPPDATA%\Packages\&lt;family&gt; — MSIX / Store app data.</summary>
    PackageData,

    /// <summary>%PROGRAMDATA%\&lt;app&gt; — all-users state.</summary>
    MachineData,
}

/// <summary>One directory belonging to an application.</summary>
public sealed record AppLocation(
    string Path,
    long AllocatedBytes,
    long FileCount,
    AppLocationKind Kind);

/// <summary>
/// Everything one application occupies, wherever it lives.
/// </summary>
/// <remarks>
/// This is the product's reason to exist. On the machine that motivated Silt, Claude Desktop
/// occupied three unrelated directories — <c>%APPDATA%\Claude</c>,
/// <c>%LOCALAPPDATA%\Packages\Claude_*</c> and <c>%LOCALAPPDATA%\Claude-3p</c>. Every
/// existing tool shows three separate folders of unremarkable size. None of them answers
/// "how much is Claude Desktop actually costing me?"
/// </remarks>
public sealed record AppFootprint(
    string Key,
    string DisplayName,
    string? Publisher,
    long TotalAllocatedBytes,
    long TotalFileCount,
    IReadOnlyList<AppLocation> Locations)
{
    /// <summary>
    /// True when the application is spread across more than one top-level directory — the
    /// case no other tool surfaces, and the one worth drawing attention to.
    /// </summary>
    public bool IsSplitAcrossLocations => Locations.Count > 1;
}
