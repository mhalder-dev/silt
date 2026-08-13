using System.Collections.Concurrent;
using System.Diagnostics;
using Silt.Core.Attribution;
using Silt.Core.Reconciliation;
using Silt.Core.Scanning;
using Silt.Core.Snapshots;

namespace Silt.Api;

/// <summary>Owns running and completed scans for the lifetime of the process.</summary>
public sealed class ScanService : IDisposable
{
    private readonly ConcurrentDictionary<string, ScanSession> _sessions = new(StringComparer.Ordinal);
    /// <summary>
    /// Snapshots kept per volume. At roughly one scan a day this is several months of
    /// history for a few tens of megabytes.
    /// </summary>
    private const int SnapshotRetentionCount = 200;

    private readonly IVolumeScanner _scanner;
    private readonly IAppAttributor _attributor;
    private readonly ISnapshotStore _snapshots;

    public ScanService(
        IVolumeScanner? scanner = null,
        IAppAttributor? attributor = null,
        ISnapshotStore? snapshots = null)
    {
        _scanner = scanner ?? new BfsScanner();
        _attributor = attributor ?? new AppAttributor();
        _snapshots = snapshots ?? new SnapshotStore();
    }

    public ScanHandleDto Start(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string id = Guid.NewGuid().ToString("N")[..12];
        var session = new ScanSession(id, rootPath);
        _sessions[id] = session;

        session.Task = Task.Run(() => RunScan(session));
        return new ScanHandleDto(id);
    }

    private void RunScan(ScanSession session)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                session.DirectoriesScanned = p.DirectoriesScanned;
                session.FilesScanned = p.FilesScanned;
                session.BytesScanned = p.BytesScanned;
                session.CurrentPath = p.CurrentPath;
            });

            ScanResult result = _scanner.Scan(
                new ScanOptions
                {
                    RootPath = session.Root,
                    Progress = progress,
                },
                session.Cancellation.Token);

            session.Result = result;

            // Reconciliation only makes sense for a whole volume; scanning a subfolder and
            // comparing it against the volume's used bytes would produce a meaningless
            // "unaccounted" figure larger than the thing being measured.
            if (IsVolumeRoot(session.Root))
            {
                try
                {
                    session.Reconciliation = VolumeReconciler.Reconcile(result, session.Root);
                }
                catch (InvalidOperationException ex)
                {
                    // A failed reconciliation must not discard a good scan.
                    session.ReconciliationError = ex.Message;
                }
            }

            CaptureSnapshot(session, result);
            session.State = ScanState.Completed;
        }
        catch (OperationCanceledException)
        {
            session.State = ScanState.Cancelled;
        }
        catch (Exception ex)
        {
            session.State = ScanState.Failed;
            session.Error = ex.Message;
        }
        finally
        {
            sw.Stop();
            session.Elapsed = sw.Elapsed;
        }
    }

    /// <summary>
    /// Records a snapshot so growth over time can be reported later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Captured automatically, because history that requires the user to remember to record
    /// it does not exist when they need it. The 44 GB temp directory that motivated this
    /// project grew silently over months; the only way to have caught it is to have been
    /// recording all along.
    /// </para>
    /// <para>
    /// Whole volumes only. Snapshots of arbitrary subfolders would not be comparable with
    /// each other, and a diff between two different roots is meaningless.
    /// </para>
    /// </remarks>
    private void CaptureSnapshot(ScanSession session, ScanResult result)
    {
        if (!IsVolumeRoot(session.Root) || session.Reconciliation is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<AppFootprint> apps = _attributor.Attribute(result);

            Snapshot snapshot = _snapshots.Capture(
                result,
                session.Reconciliation.VolumeRoot,
                session.Reconciliation.CapacityBytes,
                session.Reconciliation.FreeBytes,
                apps);

            _snapshots.Save(snapshot);
            _snapshots.Prune(session.Reconciliation.VolumeRoot, SnapshotRetentionCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience. Failing to record it must never cost the user the
            // scan they actually asked for.
            session.SnapshotError = ex.Message;
        }
    }

    private static bool IsVolumeRoot(string path)
    {
        string full = Path.GetFullPath(path);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase);
    }

    public ScanStatusDto? GetStatus(string scanId)
    {
        if (!_sessions.TryGetValue(scanId, out ScanSession? s))
        {
            return null;
        }

        return new ScanStatusDto(
            s.Id,
            s.State,
            s.Root,
            s.DirectoriesScanned,
            s.FilesScanned,
            s.BytesScanned,
            s.CurrentPath,
            s.Elapsed.TotalSeconds,
            s.Error);
    }

    public ScanSummaryDto? GetSummary(string scanId)
    {
        if (!_sessions.TryGetValue(scanId, out ScanSession? s) || s.Result is null)
        {
            return null;
        }

        ScanResult r = s.Result;
        return new ScanSummaryDto(
            s.Id,
            s.Root,
            r.Duration.TotalSeconds,
            r.TotalFiles,
            r.TotalDirectories,
            r.TotalAllocatedBytes,
            r.TotalLogicalBytes,
            r.AccessDeniedCount,
            r.FailedCount,
            r.SkippedSurrogateCount,
            r.HardLinkFilesDeduplicated,
            r.HardLinkBytesDeduplicated,
            MapReconciliation(s.Reconciliation));
    }

    private static ReconciliationDto? MapReconciliation(VolumeReconciliation? v) =>
        v is null
            ? null
            : new ReconciliationDto(
                v.VolumeRoot,
                v.CapacityBytes,
                v.FreeBytes,
                v.UsedBytes,
                v.ScannedBytes,
                v.UnaccountedBytes,
                v.UnaccountedFraction,
                v.InaccessibleDirectoryCount,
                [.. v.Lines.Select(l =>
                    new ReconciliationLineDto(l.Label, l.Bytes, l.Kind.ToString(), l.Detail))]);

    /// <summary>
    /// Returns the children of <paramref name="path"/> within a completed scan, largest
    /// first.
    /// </summary>
    /// <remarks>
    /// Capped deliberately. A directory can hold hundreds of thousands of entries, and the
    /// renderer neither needs nor can usefully draw them. The cap is reported so the UI can
    /// say so rather than silently implying it showed everything.
    /// </remarks>
    public TreeResponseDto? GetTree(string scanId, string? path, int limit = 500)
    {
        if (!_sessions.TryGetValue(scanId, out ScanSession? s) || s.Result is null)
        {
            return null;
        }

        ScanNode? node = Resolve(s.Result.Root, path);
        if (node is null)
        {
            return null;
        }

        ScanNode[] children = node.Children ?? [];
        int total = children.Length;

        // BuildPath walks the parent chain, so it is called only for the page actually
        // returned - never for every node in the tree.
        var page = children
            .OrderByDescending(c => c.TotalAllocatedBytes)
            .Take(limit)
            .Select(c => new TreeNodeDto(
                c.Name,
                c.BuildPath(),
                c.TotalAllocatedBytes,
                c.TotalLogicalBytes,
                c.TotalFileCount,
                c.TotalDirectoryCount,
                c.Children is { Length: > 0 },
                DescribeConditions(c.Condition)))
            .ToList();

        return new TreeResponseDto(
            node.BuildPath(),
            node.TotalAllocatedBytes,
            page,
            total,
            total > limit);
    }

    private static List<string> DescribeConditions(NodeCondition condition)
    {
        if (condition == NodeCondition.None)
        {
            return [];
        }

        var list = new List<string>(2);
        if (condition.HasFlag(NodeCondition.AccessDenied)) { list.Add("access-denied"); }
        if (condition.HasFlag(NodeCondition.NameSurrogate)) { list.Add("junction"); }
        if (condition.HasFlag(NodeCondition.CloudPlaceholder)) { list.Add("cloud"); }
        if (condition.HasFlag(NodeCondition.Vanished)) { list.Add("vanished"); }
        if (condition.HasFlag(NodeCondition.Failed)) { list.Add("failed"); }
        return list;
    }

    /// <summary>
    /// Projects the subtree at <paramref name="path"/> into flattened treemap rectangles.
    /// </summary>
    /// <remarks>
    /// The projection is bounded by <see cref="TreemapOptions"/> rather than by a row limit:
    /// a treemap's unit of interest is area, not rows, so what has to be capped is how much
    /// area is worth resolving separately, and how large the response is allowed to get.
    /// </remarks>
    public TreemapResponseDto? GetTreemap(string scanId, string? path)
    {
        if (!_sessions.TryGetValue(scanId, out ScanSession? s) || s.Result is null)
        {
            return null;
        }

        ScanNode? node = Resolve(s.Result.Root, path);
        if (node is null)
        {
            return null;
        }

        // BuildPath is called once here, for the view root. Descendants carry bare segments
        // and the renderer rebuilds paths by walking parents, which is what keeps a
        // 20,000-node response from being mostly repeated path prefixes.
        string viewPath = node.BuildPath();
        TreemapProjection projection = TreemapProjector.Project(node);

        var nodes = new List<TreemapNodeDto>(projection.Nodes.Count);
        for (int i = 0; i < projection.Nodes.Count; i++)
        {
            TreemapNode n = projection.Nodes[i];
            List<string> conditions = DescribeConditions(n.Condition);
            nodes.Add(new TreemapNodeDto(
                n.ParentIndex,
                // The view root's Name is its full path inside the scan tree, but the response
                // already carries that as Path; repeating it in the node would make the root
                // rectangle's label the whole path.
                i == 0 ? DisplayNameOf(viewPath) : n.Name,
                n.Bytes,
                n.Kind.ToString(),
                n.Expandable,
                conditions.Count > 0 ? conditions : null));
        }

        return new TreemapResponseDto(
            viewPath,
            projection.TotalBytes,
            projection.MinimumBytes,
            projection.AggregatedNodeCount,
            projection.Truncated,
            nodes);
    }

    private static string DisplayNameOf(string fullPath)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }

    private static ScanNode? Resolve(ScanNode root, string? path) =>
        string.IsNullOrWhiteSpace(path) ? root : ScanTree.Find(root, path);

    /// <summary>
    /// Per-application footprints for a completed scan.
    /// </summary>
    /// <remarks>
    /// Computed on demand rather than during the scan: attribution walks only the handful of
    /// well-known roots, so it costs milliseconds, and keeping it out of the scan path means
    /// a failure here can never cost the user a completed scan.
    /// </remarks>
    public AppsResponseDto? GetApps(string scanId, long minimumBytes)
    {
        if (!_sessions.TryGetValue(scanId, out ScanSession? s) || s.Result is null)
        {
            return null;
        }

        IReadOnlyList<AppFootprint> apps = _attributor.Attribute(s.Result, minimumBytes);

        return new AppsResponseDto(
            [.. apps.Select(a => new AppFootprintDto(
                a.Key,
                a.DisplayName,
                a.Publisher,
                a.TotalAllocatedBytes,
                a.TotalFileCount,
                a.IsSplitAcrossLocations,
                [.. a.Locations.Select(l => new AppLocationDto(
                    l.Path, l.AllocatedBytes, l.FileCount, l.Kind.ToString()))]))],
            minimumBytes,
            apps.Sum(a => a.TotalAllocatedBytes));
    }

    /// <summary>
    /// Compares this scan's snapshot with the recorded snapshot closest to
    /// <paramref name="days"/> ago.
    /// </summary>
    public GrowthDto? GetGrowth(string scanId, double days)
    {
        if (!_sessions.TryGetValue(scanId, out ScanSession? s) || s.Result is null)
        {
            return null;
        }

        if (s.Reconciliation is null)
        {
            return Unavailable(
                "History is recorded for whole volumes only, so a scan of a single folder " +
                "cannot be compared over time.", 0);
        }

        string volume = s.Reconciliation.VolumeRoot;
        IReadOnlyList<SnapshotInfo> history = _snapshots.List(volume);

        if (history.Count < 2)
        {
            return Unavailable(
                "This is the first recorded scan of this volume. Scan again in a few days " +
                "and Silt will show what changed.", history.Count);
        }

        SnapshotInfo? baselineInfo = GrowthAnalyzer.FindComparisonBaseline(
            history, TimeSpan.FromDays(days), DateTimeOffset.UtcNow);

        if (baselineInfo is null)
        {
            return Unavailable("No earlier snapshot is available to compare against.", history.Count);
        }

        Snapshot? baseline = _snapshots.Load(volume, baselineInfo.Id);
        Snapshot? latest = _snapshots.Load(volume, history[0].Id);

        if (baseline is null || latest is null)
        {
            return Unavailable("A snapshot could not be read.", history.Count);
        }

        GrowthReport report = GrowthAnalyzer.Compare(baseline, latest);

        return new GrowthDto(
            Available: true,
            Unavailable: null,
            FromTakenAt: report.FromTakenAt,
            ToTakenAt: report.ToTakenAt,
            SpanDays: report.Span.TotalDays,
            FromTotalBytes: report.FromTotalBytes,
            ToTotalBytes: report.ToTotalBytes,
            DeltaBytes: report.DeltaBytes,
            FreeDeltaBytes: report.FreeDeltaBytes,
            FloorsDiffer: report.FloorsDiffer,
            SnapshotCount: history.Count,
            Directories: [.. report.Directories.Take(40).Select(d => new DirectoryChangeDto(
                d.Path, d.BeforeBytes, d.AfterBytes, d.DeltaBytes, d.SelfDeltaBytes,
                d.Kind.ToString()))],
            Apps: [.. report.Apps.Take(25).Select(a => new AppChangeDto(
                a.Key, a.DisplayName, a.BeforeBytes, a.AfterBytes, a.DeltaBytes,
                a.Kind.ToString()))]);

        static GrowthDto Unavailable(string reason, int snapshotCount) => new(
            false, reason, null, null, 0, 0, 0, 0, 0, false, snapshotCount, [], []);
    }

    public bool Cancel(string scanId)
    {
        if (!_sessions.TryGetValue(scanId, out ScanSession? s))
        {
            return false;
        }
        s.Cancellation.Cancel();
        return true;
    }

    public void Dispose()
    {
        foreach (ScanSession s in _sessions.Values)
        {
            s.Cancellation.Cancel();
            s.Cancellation.Dispose();
        }
        _sessions.Clear();
    }

    private sealed class ScanSession(string id, string root)
    {
        public string Id { get; } = id;
        public string Root { get; } = root;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }

        public volatile ScanState State = ScanState.Running;
        public string? Error { get; set; }
        public string? ReconciliationError { get; set; }
        public string? SnapshotError { get; set; }

        public long DirectoriesScanned;
        public long FilesScanned;
        public long BytesScanned;
        public string CurrentPath = string.Empty;
        public TimeSpan Elapsed { get; set; }

        public ScanResult? Result { get; set; }
        public VolumeReconciliation? Reconciliation { get; set; }
    }
}
