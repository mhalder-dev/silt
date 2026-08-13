using System.Globalization;
using Silt.Core.Snapshots;

namespace Silt.Core.Tests;

public sealed class GrowthAnalyzerTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    private static Snapshot MakeSnapshot(
        DateTimeOffset takenAt,
        (string Path, long Bytes)[] directories,
        (string Key, string Name, long Bytes)[]? apps = null,
        long free = 100 * Gib) =>
        new(
            Id: takenAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            TakenAt: takenAt,
            VolumeRoot: @"C:\",
            CapacityBytes: 400 * Gib,
            FreeBytes: free,
            TotalAllocatedBytes: directories.Length > 0 ? directories[0].Bytes : 0,
            TotalFiles: 1000,
            TotalDirectories: directories.Length,
            EntryFloorBytes: 8 * Mib,
            Directories: [.. directories.Select(d => new SnapshotEntry(d.Path, d.Bytes, 1))],
            Apps: [.. (apps ?? []).Select(a => new SnapshotApp(a.Key, a.Name, a.Bytes))]);

    [Fact]
    public void Compare_AttributesGrowthToTheDirectoryThatCausedIt()
    {
        // Temp grows 12 GiB. Every ancestor's total grows by the same 12 GiB, so ranking on
        // raw delta would report five directories that all "grew 12 GiB" and bury the one
        // that actually did. Self-delta must isolate Temp.
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t0.AddDays(7);

        Snapshot before = MakeSnapshot(t0, [
            (@"C:\", 100 * Gib),
            (@"C:\Users", 60 * Gib),
            (@"C:\Users\bob", 50 * Gib),
            (@"C:\Users\bob\AppData", 40 * Gib),
            (@"C:\Users\bob\AppData\Local", 30 * Gib),
            (@"C:\Users\bob\AppData\Local\Temp", 2 * Gib),
        ]);

        Snapshot after = MakeSnapshot(t1, [
            (@"C:\", 112 * Gib),
            (@"C:\Users", 72 * Gib),
            (@"C:\Users\bob", 62 * Gib),
            (@"C:\Users\bob\AppData", 52 * Gib),
            (@"C:\Users\bob\AppData\Local", 42 * Gib),
            (@"C:\Users\bob\AppData\Local\Temp", 14 * Gib),
        ]);

        GrowthReport report = GrowthAnalyzer.Compare(before, after);

        DirectoryChange top = report.Directories[0];
        Assert.Equal(@"C:\Users\bob\AppData\Local\Temp", top.Path);
        Assert.Equal(12 * Gib, top.SelfDeltaBytes);

        // The ancestors passed the whole change downward, so none of them is significant.
        Assert.Single(report.Directories);
    }

    [Fact]
    public void Compare_SplitsGrowthBetweenSiblings()
    {
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Snapshot before = MakeSnapshot(t0, [
            (@"C:\", 10 * Gib),
            (@"C:\a", 5 * Gib),
            (@"C:\b", 5 * Gib),
        ]);

        Snapshot after = MakeSnapshot(t0.AddDays(1), [
            (@"C:\", 16 * Gib),
            (@"C:\a", 9 * Gib),
            (@"C:\b", 7 * Gib),
        ]);

        GrowthReport report = GrowthAnalyzer.Compare(before, after);

        Assert.Equal(2, report.Directories.Count);
        Assert.Equal(@"C:\a", report.Directories[0].Path);
        Assert.Equal(4 * Gib, report.Directories[0].SelfDeltaBytes);
        Assert.Equal(2 * Gib, report.Directories[1].SelfDeltaBytes);
    }

    [Fact]
    public void Compare_KeepsGrowthOfUnrecordedChildrenInTheNearestAncestor()
    {
        // A parent grows because of children that sit below the snapshot floor and were
        // never recorded. That growth must stay attributed to the parent rather than
        // disappearing because no child accounts for it.
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Snapshot before = MakeSnapshot(t0, [(@"C:\", 10 * Gib), (@"C:\cache", 1 * Gib)]);
        Snapshot after = MakeSnapshot(t0.AddDays(1), [(@"C:\", 15 * Gib), (@"C:\cache", 6 * Gib)]);

        GrowthReport report = GrowthAnalyzer.Compare(before, after);

        DirectoryChange cache = Assert.Single(report.Directories, d => d.Path == @"C:\cache");
        Assert.Equal(5 * Gib, cache.SelfDeltaBytes);
    }

    [Fact]
    public void Compare_ClassifiesAddedRemovedGrownAndShrunk()
    {
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Snapshot before = MakeSnapshot(t0, [
            (@"C:\", 10 * Gib),
            (@"C:\gone", 3 * Gib),
            (@"C:\shrinking", 5 * Gib),
        ]);

        Snapshot after = MakeSnapshot(t0.AddDays(1), [
            (@"C:\", 10 * Gib),
            (@"C:\fresh", 4 * Gib),
            (@"C:\shrinking", 1 * Gib),
        ]);

        GrowthReport report = GrowthAnalyzer.Compare(before, after);

        Assert.Equal(ChangeKind.Removed, Single(report, @"C:\gone").Kind);
        Assert.Equal(ChangeKind.Added, Single(report, @"C:\fresh").Kind);
        Assert.Equal(ChangeKind.Shrunk, Single(report, @"C:\shrinking").Kind);
    }

    [Fact]
    public void Compare_IgnoresChangesBelowTheSignificanceFloor()
    {
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Snapshot before = MakeSnapshot(t0, [(@"C:\", 10 * Gib), (@"C:\noise", 1 * Gib)]);
        Snapshot after = MakeSnapshot(t0.AddDays(1),
            [(@"C:\", 10 * Gib), (@"C:\noise", 1 * Gib + (2 * Mib))]);

        GrowthReport report = GrowthAnalyzer.Compare(before, after);

        Assert.Empty(report.Directories);
    }

    [Fact]
    public void Compare_ReportsApplicationLevelChanges()
    {
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Snapshot before = MakeSnapshot(t0, [(@"C:\", 10 * Gib)],
            apps: [("claude", "Claude", 12 * Gib), ("vscode", "VS Code", 1 * Gib)]);
        Snapshot after = MakeSnapshot(t0.AddDays(7), [(@"C:\", 10 * Gib)],
            apps: [("claude", "Claude", 19 * Gib), ("vscode", "VS Code", 1 * Gib)]);

        GrowthReport report = GrowthAnalyzer.Compare(before, after);

        AppChange claude = Assert.Single(report.Apps);
        Assert.Equal("Claude", claude.DisplayName);
        Assert.Equal(7 * Gib, claude.DeltaBytes);
        Assert.Equal(ChangeKind.Grown, claude.Kind);
    }

    [Fact]
    public void Compare_FlagsMismatchedSnapshotFloors()
    {
        // Diffing across a floor change would report directories as "new" when they merely
        // crossed the threshold, so the mismatch is surfaced rather than hidden.
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Snapshot before = MakeSnapshot(t0, [(@"C:\", 10 * Gib)]) with { EntryFloorBytes = 1 * Mib };
        Snapshot after = MakeSnapshot(t0.AddDays(1), [(@"C:\", 10 * Gib)]);

        Assert.True(GrowthAnalyzer.Compare(before, after).FloorsDiffer);
    }

    [Fact]
    public void FindComparisonBaseline_PicksTheSnapshotNearestTheRequestedAge()
    {
        DateTimeOffset now = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
        SnapshotInfo[] history =
        [
            new("d0", now, @"C:\", 0, 0),
            new("d6", now.AddDays(-6), @"C:\", 0, 0),
            new("d20", now.AddDays(-20), @"C:\", 0, 0),
        ];

        SnapshotInfo? baseline =
            GrowthAnalyzer.FindComparisonBaseline(history, TimeSpan.FromDays(7), now);

        Assert.Equal("d6", baseline?.Id);
    }

    [Fact]
    public void FindComparisonBaseline_NeedsAtLeastTwoSnapshots()
    {
        DateTimeOffset now = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
        SnapshotInfo[] history = [new("only", now, @"C:\", 0, 0)];

        Assert.Null(GrowthAnalyzer.FindComparisonBaseline(history, TimeSpan.FromDays(7), now));
    }

    private static DirectoryChange Single(GrowthReport report, string path) =>
        Assert.Single(report.Directories, d =>
            string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
}

