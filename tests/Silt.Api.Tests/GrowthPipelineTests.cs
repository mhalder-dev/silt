using Silt.Api;
using Silt.Core.Scanning;
using Silt.Core.Snapshots;

namespace Silt.Api.Tests;

/// <summary>
/// End-to-end verification of the growth pipeline: a scan is recorded automatically, a
/// second scan is compared against the first, and the change is attributed to the folder
/// that actually caused it.
/// </summary>
/// <remarks>
/// Uses a fake scanner so a synthetic tree can be produced for a real volume root, and a
/// temporary snapshot directory so the developer's own history is untouched. Reconciliation
/// still reads genuine volume geometry, which is what makes the whole-volume path exercise
/// the same code the app runs.
/// </remarks>
public sealed class GrowthPipelineTests : IDisposable
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    private readonly string _storeRoot;
    private readonly SnapshotStore _store;
    private readonly string _volumeRoot;

    public GrowthPipelineTests()
    {
        _storeRoot = Path.Combine(Path.GetTempPath(), "silt-growth", Guid.NewGuid().ToString("N"));
        _store = new SnapshotStore(_storeRoot);
        _volumeRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
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

    /// <summary>Returns a fixed tree so a "change on disk" can be simulated exactly.</summary>
    private sealed class FakeScanner(Func<ScanResult> factory) : IVolumeScanner
    {
        public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken = default)
            => factory();
    }

    private ScanResult TreeWithTempSize(long tempBytes)
    {
        var root = new ScanNode { Name = _volumeRoot };
        var users = new ScanNode { Name = "Users", Parent = root };
        var profile = new ScanNode { Name = "someone", Parent = users };
        var appData = new ScanNode { Name = "AppData", Parent = profile };
        var local = new ScanNode { Name = "Local", Parent = appData };
        var temp = new ScanNode { Name = "Temp", Parent = local, TotalAllocatedBytes = tempBytes };
        var windows = new ScanNode { Name = "Windows", Parent = root, TotalAllocatedBytes = 25 * Gib };

        local.Children = [temp];
        appData.Children = [local];
        profile.Children = [appData];
        users.Children = [profile];
        root.Children = [users, windows];

        // Totals roll up the way a real scan would.
        local.TotalAllocatedBytes = tempBytes;
        appData.TotalAllocatedBytes = tempBytes;
        profile.TotalAllocatedBytes = tempBytes;
        users.TotalAllocatedBytes = tempBytes;
        root.TotalAllocatedBytes = tempBytes + (25 * Gib);

        return new ScanResult
        {
            Root = root,
            Duration = TimeSpan.FromSeconds(1),
            TotalAllocatedBytes = root.TotalAllocatedBytes,
            TotalFiles = 1000,
            TotalDirectories = 6,
        };
    }

    private async Task<string> RunScanAsync(ScanService service)
    {
        ScanHandleDto handle = service.Start(_volumeRoot);

        for (int i = 0; i < 200; i++)
        {
            ScanStatusDto? status = service.GetStatus(handle.ScanId);
            if (status is { State: ScanState.Completed })
            {
                return handle.ScanId;
            }
            if (status is { State: ScanState.Failed })
            {
                Assert.Fail($"Scan failed: {status.Error}");
            }
            await Task.Delay(25);
        }

        Assert.Fail("Scan did not complete within the timeout.");
        return string.Empty;
    }

    [Fact]
    public async Task FirstScan_RecordsASnapshotAndReportsNothingToCompare()
    {
        using var service = new ScanService(
            new FakeScanner(() => TreeWithTempSize(2 * Gib)), attributor: null, snapshots: _store);

        string scanId = await RunScanAsync(service);

        GrowthDto? growth = service.GetGrowth(scanId, days: 7);

        Assert.NotNull(growth);
        Assert.False(growth.Available);
        Assert.Equal(1, growth.SnapshotCount);
        Assert.Contains("first recorded scan", growth.Unavailable!, StringComparison.OrdinalIgnoreCase);

        // The snapshot must exist even though there is nothing to compare it with; that is
        // the whole point of recording automatically.
        Assert.Single(_store.List(_volumeRoot));
    }

    [Fact]
    public async Task SecondScan_AttributesGrowthToTheFolderThatGrew()
    {
        // This is the scenario that motivated the product: a temp directory quietly gaining
        // 12 GiB between two scans, with every ancestor's total moving by the same amount.
        long tempBytes = 2 * Gib;

        using var service = new ScanService(
            new FakeScanner(() => TreeWithTempSize(tempBytes)), attributor: null, snapshots: _store);

        await RunScanAsync(service);

        tempBytes = 14 * Gib;
        string secondScanId = await RunScanAsync(service);

        GrowthDto? growth = service.GetGrowth(secondScanId, days: 7);

        Assert.NotNull(growth);
        Assert.True(growth.Available, growth.Unavailable);
        Assert.Equal(2, growth.SnapshotCount);
        Assert.Equal(12 * Gib, growth.DeltaBytes);

        DirectoryChangeDto top = growth.Directories[0];
        Assert.EndsWith(@"AppData\Local\Temp", top.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12 * Gib, top.SelfDeltaBytes);
        Assert.Equal("Grown", top.Kind);

        // Ancestors passed the change straight down, so nothing else is significant.
        Assert.Single(growth.Directories);
    }

    [Fact]
    public async Task ShrinkingIsReportedAsWellAsGrowth()
    {
        long tempBytes = 20 * Gib;

        using var service = new ScanService(
            new FakeScanner(() => TreeWithTempSize(tempBytes)), attributor: null, snapshots: _store);

        await RunScanAsync(service);

        // The user cleans up, which should read as a negative delta rather than as nothing.
        tempBytes = 1 * Gib;
        string secondScanId = await RunScanAsync(service);

        GrowthDto? growth = service.GetGrowth(secondScanId, days: 7);

        Assert.NotNull(growth);
        Assert.True(growth.Available, growth.Unavailable);
        Assert.Equal(-19 * Gib, growth.DeltaBytes);
        Assert.Equal("Shrunk", growth.Directories[0].Kind);
        Assert.Equal(-19 * Gib, growth.Directories[0].SelfDeltaBytes);
    }

    [Fact]
    public async Task GrowthIsUnavailableForASubfolderScan()
    {
        // History is comparable only for whole volumes; diffing a subfolder scan against a
        // volume snapshot would produce a meaningless number.
        using var service = new ScanService(
            new FakeScanner(() => TreeWithTempSize(2 * Gib)), attributor: null, snapshots: _store);

        string subfolder = Path.Combine(Path.GetTempPath(), "silt-sub", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subfolder);

        try
        {
            ScanHandleDto handle = service.Start(subfolder);
            for (int i = 0; i < 200 && service.GetStatus(handle.ScanId)?.State == ScanState.Running; i++)
            {
                await Task.Delay(25);
            }

            GrowthDto? growth = service.GetGrowth(handle.ScanId, days: 7);

            Assert.NotNull(growth);
            Assert.False(growth.Available);
            Assert.Contains("whole volumes", growth.Unavailable!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(subfolder, recursive: true);
        }
    }
}
