namespace Silt.Core.Snapshots;

public enum ChangeKind
{
    Added,
    Removed,
    Grown,
    Shrunk,
    Unchanged,
}

/// <summary>How one directory changed between two snapshots.</summary>
/// <param name="SelfDeltaBytes">
/// The change originating in this directory's own files, with every recorded child's change
/// subtracted. This is the number worth ranking on.
/// </param>
public sealed record DirectoryChange(
    string Path,
    long BeforeBytes,
    long AfterBytes,
    long DeltaBytes,
    long SelfDeltaBytes,
    ChangeKind Kind);

/// <summary>How one application's footprint changed between two snapshots.</summary>
public sealed record AppChange(
    string Key,
    string DisplayName,
    long BeforeBytes,
    long AfterBytes,
    long DeltaBytes,
    ChangeKind Kind);

/// <summary>The result of comparing two snapshots.</summary>
public sealed record GrowthReport(
    DateTimeOffset FromTakenAt,
    DateTimeOffset ToTakenAt,
    TimeSpan Span,
    long FromTotalBytes,
    long ToTotalBytes,
    long DeltaBytes,
    long FromFreeBytes,
    long ToFreeBytes,
    long FreeDeltaBytes,
    bool FloorsDiffer,
    IReadOnlyList<DirectoryChange> Directories,
    IReadOnlyList<AppChange> Apps);

/// <summary>Compares snapshots to explain what changed and where.</summary>
public static class GrowthAnalyzer
{
    /// <summary>
    /// Changes smaller than this are noise on a developer machine, where caches move by
    /// megabytes constantly.
    /// </summary>
    public const long DefaultSignificanceBytes = 16L * 1024 * 1024;

    public static GrowthReport Compare(
        Snapshot from,
        Snapshot to,
        long significanceBytes = DefaultSignificanceBytes)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        Dictionary<string, SnapshotEntry> before =
            from.Directories.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SnapshotEntry> after =
            to.Directories.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);

        // Raw delta per path, across the union of both snapshots.
        var deltas = new Dictionary<string, long>(
            before.Count + after.Count, StringComparer.OrdinalIgnoreCase);

        foreach (string path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            long b = before.TryGetValue(path, out SnapshotEntry? bv) ? bv.AllocatedBytes : 0;
            long a = after.TryGetValue(path, out SnapshotEntry? av) ? av.AllocatedBytes : 0;
            deltas[path] = a - b;
        }

        Dictionary<string, long> childDeltaSums = SumDeltasByParent(deltas);

        var directories = new List<DirectoryChange>();
        foreach ((string path, long delta) in deltas)
        {
            long selfDelta = delta - (childDeltaSums.TryGetValue(path, out long sum) ? sum : 0);

            // Rank on self-delta so a 12 GiB jump is reported against the directory that
            // actually grew, not against it AND every ancestor up to the volume root.
            if (Math.Abs(selfDelta) < significanceBytes)
            {
                continue;
            }

            long b = before.TryGetValue(path, out SnapshotEntry? bv) ? bv.AllocatedBytes : 0;
            long a = after.TryGetValue(path, out SnapshotEntry? av) ? av.AllocatedBytes : 0;

            directories.Add(new DirectoryChange(
                path, b, a, delta, selfDelta,
                Classify(before.ContainsKey(path), after.ContainsKey(path), delta)));
        }

        var apps = new List<AppChange>();
        Dictionary<string, SnapshotApp> appsBefore =
            from.Apps.ToDictionary(a => a.Key, StringComparer.Ordinal);
        Dictionary<string, SnapshotApp> appsAfter =
            to.Apps.ToDictionary(a => a.Key, StringComparer.Ordinal);

        foreach (string key in appsBefore.Keys.Union(appsAfter.Keys, StringComparer.Ordinal))
        {
            long b = appsBefore.TryGetValue(key, out SnapshotApp? bv) ? bv.AllocatedBytes : 0;
            long a = appsAfter.TryGetValue(key, out SnapshotApp? av) ? av.AllocatedBytes : 0;
            long delta = a - b;

            if (Math.Abs(delta) < significanceBytes)
            {
                continue;
            }

            apps.Add(new AppChange(
                key,
                (appsAfter.TryGetValue(key, out SnapshotApp? name) ? name : appsBefore[key]).DisplayName,
                b, a, delta,
                Classify(appsBefore.ContainsKey(key), appsAfter.ContainsKey(key), delta)));
        }

        return new GrowthReport(
            from.TakenAt,
            to.TakenAt,
            to.TakenAt - from.TakenAt,
            from.TotalAllocatedBytes,
            to.TotalAllocatedBytes,
            to.TotalAllocatedBytes - from.TotalAllocatedBytes,
            from.FreeBytes,
            to.FreeBytes,
            to.FreeBytes - from.FreeBytes,
            from.EntryFloorBytes != to.EntryFloorBytes,
            [.. directories.OrderByDescending(d => Math.Abs(d.SelfDeltaBytes))],
            [.. apps.OrderByDescending(a => Math.Abs(a.DeltaBytes))]);
    }

    /// <summary>
    /// Totals each path's direct children's deltas, so a parent's own contribution can be
    /// isolated.
    /// </summary>
    /// <remarks>
    /// Only <em>recorded</em> children are subtracted. Directories below the snapshot floor
    /// are absent, so their growth correctly remains part of the nearest recorded ancestor's
    /// self-delta rather than vanishing from the report.
    /// </remarks>
    private static Dictionary<string, long> SumDeltasByParent(Dictionary<string, long> deltas)
    {
        var sums = new Dictionary<string, long>(deltas.Count, StringComparer.OrdinalIgnoreCase);

        foreach ((string path, long delta) in deltas)
        {
            string? parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
            {
                continue;
            }

            // Path.GetDirectoryName("C:\Users") is "C:\", which is how the volume root is
            // recorded, so no normalization is needed for the top level.
            if (deltas.ContainsKey(parent))
            {
                sums[parent] = (sums.TryGetValue(parent, out long existing) ? existing : 0) + delta;
            }
        }

        return sums;
    }

    private static ChangeKind Classify(bool existedBefore, bool existsAfter, long delta) =>
        (existedBefore, existsAfter) switch
        {
            (false, true) => ChangeKind.Added,
            (true, false) => ChangeKind.Removed,
            _ when delta > 0 => ChangeKind.Grown,
            _ when delta < 0 => ChangeKind.Shrunk,
            _ => ChangeKind.Unchanged,
        };

    /// <summary>
    /// Picks the snapshot closest to <paramref name="age"/> before the newest one, for
    /// answering "what changed this week".
    /// </summary>
    /// <remarks>
    /// Nearest-match rather than exact: snapshots accumulate whenever the user happens to
    /// scan, so demanding one from exactly seven days ago would usually find nothing. The
    /// report states the span actually compared instead of implying a precise week.
    /// </remarks>
    public static SnapshotInfo? FindComparisonBaseline(
        IReadOnlyList<SnapshotInfo> history, TimeSpan age, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count < 2)
        {
            return null;
        }

        DateTimeOffset newest = history.Max(h => h.TakenAt);
        DateTimeOffset target = now - age;

        return history
            .Where(h => h.TakenAt < newest)
            .OrderBy(h => Math.Abs((h.TakenAt - target).Ticks))
            .FirstOrDefault();
    }
}
