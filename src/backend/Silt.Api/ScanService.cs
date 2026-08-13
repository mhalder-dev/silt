using System.Collections.Concurrent;
using System.Diagnostics;
using Silt.Core.Attribution;
using Silt.Core.Reconciliation;
using Silt.Core.Scanning;

namespace Silt.Api;

/// <summary>Owns running and completed scans for the lifetime of the process.</summary>
public sealed class ScanService : IDisposable
{
    private readonly ConcurrentDictionary<string, ScanSession> _sessions = new(StringComparer.Ordinal);
    private readonly IVolumeScanner _scanner;
    private readonly IAppAttributor _attributor;

    public ScanService(IVolumeScanner? scanner = null, IAppAttributor? attributor = null)
    {
        _scanner = scanner ?? new BfsScanner();
        _attributor = attributor ?? new AppAttributor();
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

        ScanNode? node = string.IsNullOrWhiteSpace(path)
            ? s.Result.Root
            : FindNode(s.Result.Root, path);

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
    /// Walks the tree to the requested path.
    /// </summary>
    /// <remarks>
    /// Compares segment by segment against names the scanner itself produced, rather than
    /// doing a string prefix match on the full path. A prefix match would let
    /// <c>C:\Users\Bob2</c> resolve under <c>C:\Users\Bob</c>.
    /// </remarks>
    private static ScanNode? FindNode(ScanNode root, string path)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        // The root node's Name IS its full path; only descendants hold a bare segment.
        string rootPath = Path.TrimEndingDirectorySeparator(root.Name);

        if (string.Equals(target, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string remainder = target[rootPath.Length..].TrimStart(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        ScanNode current = root;
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            ScanNode? next = current.Children?.FirstOrDefault(
                c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return null;
            }
            current = next;
        }

        return current;
    }

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

        public long DirectoriesScanned;
        public long FilesScanned;
        public long BytesScanned;
        public string CurrentPath = string.Empty;
        public TimeSpan Elapsed { get; set; }

        public ScanResult? Result { get; set; }
        public VolumeReconciliation? Reconciliation { get; set; }
    }
}
