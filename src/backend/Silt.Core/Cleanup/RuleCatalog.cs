namespace Silt.Core.Cleanup;

/// <summary>
/// The rules Silt ships with.
/// </summary>
/// <remarks>
/// <para>
/// Six rules, not fifty. On the machine that motivated the project these six covered roughly
/// 75 GB of the ~100 GB that was actually reclaimable; the long tail of plausible-sounding
/// cleaners covered single-digit gigabytes between them while each still costing detection,
/// estimation, a destructive test and a documentation entry.
/// </para>
/// <para>
/// Everything else Silt finds is <b>advisory</b>: it is named, sized and explained, and the
/// user acts in Explorer. That is a fraction of the risk for most of the value.
/// </para>
/// </remarks>
public static class RuleCatalog
{
    private const string LocalAppData = "%LOCALAPPDATA%";
    private const string AppData = "%APPDATA%";
    private const string UserProfile = "%USERPROFILE%";

    public static IReadOnlyList<CleanupRule> All { get; } =
    [
        new CleanupRule(
            id: "temp.user.aged",
            displayName: "Temporary files older than 7 days",
            description:
                "Windows and every application write scratch files here and frequently never " +
                "remove them. This is usually the single largest reclaimable area on a " +
                "developer machine.",
            tier: SafetyTier.AlwaysSafe,
            targets: [new RuleTarget($@"{LocalAppData}\Temp", RuleTargetKind.DirectoryContents)],
            regeneration: new Regeneration(
                "Applications recreate what they need on demand. Nothing here is expected to " +
                "survive a reboot."),
            // Seven days, not zero: anything younger may belong to a process running right
            // now, including an installer midway through its work.
            minimumAge: TimeSpan.FromDays(7)),

        new CleanupRule(
            id: "npm.cache",
            displayName: "npm package cache",
            description:
                "npm keeps every package version it has ever downloaded, and never prunes it. " +
                "It grows without bound on a machine that builds JavaScript projects.",
            tier: SafetyTier.SafeWithCaveat,
            targets:
            [
                new RuleTarget($@"{LocalAppData}\npm-cache\_cacache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{AppData}\npm-cache\_cacache", RuleTargetKind.DirectoryContents),
            ],
            regeneration: new Regeneration(
                "Packages are re-downloaded on the next install, so the first build afterwards " +
                "is slower and needs a network connection.",
                "npm cache clean --force")),

        new CleanupRule(
            id: "chrome.cache",
            displayName: "Chrome browsing caches",
            description:
                "Cached page resources for each Chrome profile. History, bookmarks, passwords " +
                "and extensions live elsewhere in the profile and are not touched.",
            tier: SafetyTier.SafeWithCaveat,
            targets:
            [
                // The * expands to each profile directory, so profiles do not have to be
                // hardcoded and new ones are picked up automatically.
                new RuleTarget($@"{LocalAppData}\Google\Chrome\User Data\*\Cache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\Google\Chrome\User Data\*\Code Cache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\Google\Chrome\User Data\*\GPUCache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\Microsoft\Edge\User Data\*\Cache", RuleTargetKind.DirectoryContents),
            ],
            regeneration: new Regeneration(
                "Pages re-download their resources, so browsing is briefly slower. You stay " +
                "signed in and nothing is removed from your history or bookmarks.")),

        new CleanupRule(
            id: "jetbrains.caches",
            displayName: "JetBrains IDE caches and indexes",
            description:
                "Project indexes and local caches for IntelliJ, Rider, PyCharm and friends. " +
                "Settings, keymaps and plugins live in a separate directory and are not touched.",
            tier: SafetyTier.SafeWithCaveat,
            targets:
            [
                new RuleTarget($@"{LocalAppData}\JetBrains\*\caches", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\JetBrains\*\index", RuleTargetKind.DirectoryContents),
            ],
            regeneration: new Regeneration(
                "The IDE re-indexes each project the next time you open it, which takes a few " +
                "minutes per project and then never again.")),

        new CleanupRule(
            id: "crashdumps.thumbcache",
            displayName: "Crash dumps and thumbnail caches",
            description:
                "Memory dumps written when an application crashed, and Explorer's thumbnail " +
                "database. Neither is useful once the crash has passed.",
            tier: SafetyTier.AlwaysSafe,
            targets:
            [
                new RuleTarget($@"{LocalAppData}\CrashDumps", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\Microsoft\Windows\Explorer",
                    RuleTargetKind.MatchingFiles, Glob: "thumbcache_*.db"),
                new RuleTarget($@"{LocalAppData}\Microsoft\Windows\Explorer",
                    RuleTargetKind.MatchingFiles, Glob: "iconcache_*.db"),
            ],
            regeneration: new Regeneration(
                "Thumbnails are regenerated as you browse folders. Crash dumps are only useful " +
                "to whoever was debugging that crash.")),

        new CleanupRule(
            id: "pkgmgr.caches",
            displayName: "Package manager download caches",
            description:
                "Downloaded package archives for NuGet, pip and uv. These are HTTP caches, not " +
                "the installed packages themselves.",
            tier: SafetyTier.SafeWithCaveat,
            targets:
            [
                // NuGet's http-cache only. NOT global-packages, which can hold locally packed
                // packages that exist on no remote and would be unrecoverable.
                new RuleTarget($@"{LocalAppData}\NuGet\v3-cache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\NuGet\plugins-cache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\pip\cache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{LocalAppData}\uv\cache", RuleTargetKind.DirectoryContents),
                new RuleTarget($@"{UserProfile}\.cache\uv", RuleTargetKind.DirectoryContents),
            ],
            regeneration: new Regeneration(
                "Packages are re-downloaded on the next restore. Installed packages are " +
                "untouched; only the download cache is cleared.",
                "dotnet nuget locals http-cache --clear")),
    ];

    public static CleanupRule? ById(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
}
