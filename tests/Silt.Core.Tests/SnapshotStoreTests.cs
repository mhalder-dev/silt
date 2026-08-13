using System.Globalization;
using Silt.Core.Attribution;
using Silt.Core.Scanning;
using Silt.Core.Snapshots;

namespace Silt.Core.Tests;

public sealed class SnapshotStoreTests : IDisposable
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    private readonly string _storeRoot;
    private readonly SnapshotStore _store;

    public SnapshotStoreTests()
    {
        _storeRoot = Path.Combine(Path.GetTempPath(), "silt-snap", Guid.NewGuid().ToString("N"));
        _store = new SnapshotStore(_storeRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_storeRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Snapshot Make(DateTimeOffset takenAt, long total) => new(
        Id: takenAt.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
        TakenAt: takenAt,
        VolumeRoot: @"C:\",
        CapacityBytes: 400 * Gib,
        FreeBytes: 100 * Gib,
        TotalAllocatedBytes: total,
        TotalFiles: 500,
        TotalDirectories: 3,
        EntryFloorBytes: 8 * Mib,
        Directories: [new SnapshotEntry(@"C:\", total, 500)],
        Apps: [new SnapshotApp("claude", "Claude", 18 * Gib)]);

    [Fact]
    public void SaveAndLoad_RoundTripsEveryField()
    {
        Snapshot original = Make(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero), 42 * Gib);
        _store.Save(original);

        Snapshot? loaded = _store.Load(@"C:\", original.Id);

        Assert.NotNull(loaded);
        Assert.Equal(original.TotalAllocatedBytes, loaded.TotalAllocatedBytes);
        Assert.Equal(original.TakenAt, loaded.TakenAt);
        Assert.Equal(original.EntryFloorBytes, loaded.EntryFloorBytes);
        Assert.Equal("Claude", Assert.Single(loaded.Apps).DisplayName);
        Assert.Equal(@"C:\", Assert.Single(loaded.Directories).Path);
    }

    [Fact]
    public void List_ReturnsNewestFirst()
    {
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        _store.Save(Make(t0, 1 * Gib));
        _store.Save(Make(t0.AddDays(2), 2 * Gib));
        _store.Save(Make(t0.AddDays(1), 3 * Gib));

        IReadOnlyList<SnapshotInfo> history = _store.List(@"C:\");

        Assert.Equal(3, history.Count);
        Assert.True(history[0].TakenAt > history[1].TakenAt);
        Assert.True(history[1].TakenAt > history[2].TakenAt);
    }

    [Fact]
    public void Prune_KeepsTheNewestAndDeletesTheRest()
    {
        DateTimeOffset t0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 6; i++)
        {
            _store.Save(Make(t0.AddDays(i), (i + 1) * Gib));
        }

        int removed = _store.Prune(@"C:\", keep: 2);

        Assert.Equal(4, removed);
        IReadOnlyList<SnapshotInfo> remaining = _store.List(@"C:\");
        Assert.Equal(2, remaining.Count);
        Assert.Equal(t0.AddDays(5), remaining[0].TakenAt);
    }

    [Theory]
    [InlineData(@"..\..\escape")]
    [InlineData(@"C:\Windows\System32\config")]
    [InlineData("sub/dir")]
    public void Load_RefusesIdsThatCouldEscapeTheSnapshotDirectory(string id)
    {
        // The id becomes a file name. A history viewer is not a reason to hand out an
        // arbitrary file read.
        Assert.Null(_store.Load(@"C:\", id));
    }

    [Fact]
    public void List_IgnoresCorruptFilesInsteadOfThrowing()
    {
        _store.Save(Make(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), 1 * Gib));

        string directory = Path.Combine(_storeRoot, "C");
        File.WriteAllText(Path.Combine(directory, "broken.json.gz"), "this is not gzip");

        // A half-written snapshot from an interrupted scan must not break the history view.
        Assert.Single(_store.List(@"C:\"));
    }

    [Fact]
    public void Capture_ProducesUniqueIdsForSnapshotsTakenInQuickSuccession()
    {
        // Regression. The id doubles as the file name, so an earlier version using
        // second resolution silently overwrote history when two scans landed in the same
        // second - after which the growth report claimed "first recorded scan" forever.
        var root = new ScanNode { Name = @"C:\", TotalAllocatedBytes = 1 * Gib };
        var scan = new ScanResult { Root = root, Duration = TimeSpan.Zero };

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 50; i++)
        {
            Snapshot snapshot = _store.Capture(scan, @"C:\", 400 * Gib, 100 * Gib, []);
            Assert.True(ids.Add(snapshot.Id), $"Duplicate snapshot id: {snapshot.Id}");
            _store.Save(snapshot);
        }

        Assert.Equal(50, _store.List(@"C:\").Count);
    }

    [Fact]
    public void Capture_KeepsLargeDirectoriesAndDropsSmallOnes()
    {
        var root = new ScanNode { Name = @"C:\", TotalAllocatedBytes = 50 * Gib, TotalFileCount = 10 };
        var big = new ScanNode { Name = "big", Parent = root, TotalAllocatedBytes = 20 * Gib };
        var small = new ScanNode { Name = "small", Parent = root, TotalAllocatedBytes = 1024 };

        // Depth 3 so the always-keep-shallow rule does not apply, isolating the size floor.
        var deepParent = new ScanNode { Name = "a", Parent = root, TotalAllocatedBytes = 30 * Gib };
        var deepBig = new ScanNode { Name = "b", Parent = deepParent, TotalAllocatedBytes = 29 * Gib };
        var deepSmall = new ScanNode { Name = "c", Parent = deepBig, TotalAllocatedBytes = 4096 };
        deepBig.Children = [deepSmall];
        deepParent.Children = [deepBig];
        root.Children = [big, small, deepParent];

        var scan = new ScanResult { Root = root, Duration = TimeSpan.Zero };
        Snapshot snapshot = _store.Capture(scan, @"C:\", 400 * Gib, 100 * Gib, []);

        HashSet<string> paths = [.. snapshot.Directories.Select(d => d.Path)];

        Assert.Contains(@"C:\big", paths);
        Assert.Contains(@"C:\a\b", paths);

        // Below the floor and deeper than the always-keep depth.
        Assert.DoesNotContain(@"C:\a\b\c", paths);
    }

    [Fact]
    public void Capture_RecordsApplicationFootprints()
    {
        var root = new ScanNode { Name = @"C:\", TotalAllocatedBytes = 1 * Gib };
        var scan = new ScanResult { Root = root, Duration = TimeSpan.Zero };

        AppFootprint[] apps =
        [
            new("claude", "Claude", null, 18 * Gib, 100, [
                new AppLocation(@"C:\Users\bob\AppData\Roaming\Claude", 11 * Gib, 50,
                    AppLocationKind.RoamingData),
            ]),
        ];

        Snapshot snapshot = _store.Capture(scan, @"C:\", 400 * Gib, 100 * Gib, apps);

        SnapshotApp recorded = Assert.Single(snapshot.Apps);
        Assert.Equal("Claude", recorded.DisplayName);
        Assert.Equal(18 * Gib, recorded.AllocatedBytes);
    }
}

