namespace Silt.Core.Scanning;

/// <summary>Notable conditions attached to a scanned directory.</summary>
/// <remarks>
/// Named <c>NodeCondition</c> rather than <c>NodeFlags</c> because CA1711 reserves the
/// <c>Flags</c> suffix; the <see cref="FlagsAttribute"/> already conveys that it combines.
/// </remarks>
[Flags]
public enum NodeCondition
{
    None = 0,

    /// <summary>The directory could not be opened. Its subtree is missing from all totals.</summary>
    AccessDenied = 1 << 0,

    /// <summary>A junction or symlink. Deliberately not traversed; it would double-count.</summary>
    NameSurrogate = 1 << 1,

    /// <summary>Cloud-tiered placeholder content lives here.</summary>
    CloudPlaceholder = 1 << 2,

    /// <summary>The directory vanished between discovery and enumeration.</summary>
    Vanished = 1 << 3,

    /// <summary>Enumeration failed for some other reason.</summary>
    Failed = 1 << 4,
}

/// <summary>A directory in the scan tree.</summary>
/// <remarks>
/// Each node is enumerated by exactly one worker, so <see cref="Children"/> and the
/// mutable totals are written by a single thread and need no synchronization. Only the
/// cross-cutting counters and the hardlink set are shared between workers.
/// </remarks>
public sealed class ScanNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public ScanNode? Parent { get; init; }

    /// <summary>Bytes of files directly in this directory, excluding subdirectories.</summary>
    public long OwnAllocatedBytes { get; set; }
    public long OwnLogicalBytes { get; set; }
    public int OwnFileCount { get; set; }

    /// <summary>Subtree totals, populated by the roll-up pass after enumeration completes.</summary>
    public long TotalAllocatedBytes { get; set; }
    public long TotalLogicalBytes { get; set; }
    public long TotalFileCount { get; set; }
    public int TotalDirectoryCount { get; set; }

    public NodeCondition Condition { get; set; }
    public int Win32Error { get; set; }

    public List<ScanNode>? Children { get; set; }

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    public override string ToString() => $"{FullPath} ({TotalAllocatedBytes:N0} bytes)";
}

/// <summary>Inputs to a scan.</summary>
public sealed class ScanOptions
{
    /// <summary>Directory to scan. A volume root such as <c>C:\</c> is normal.</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// De-duplicate files sharing a file id so hardlinked content is counted once.
    /// Costs roughly 8 bytes per distinct file. Leaving this off over-reports
    /// <c>C:\Windows\WinSxS</c> by roughly 2x.
    /// </summary>
    public bool DeduplicateHardLinks { get; init; } = true;

    /// <summary>Worker count. Defaults to the processor count, clamped to a sane range.</summary>
    public int DegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 32);

    /// <summary>Invoked periodically with progress. May be called from any thread.</summary>
    public IProgress<ScanProgress>? Progress { get; init; }
}

/// <summary>Progress ticket. Deliberately a struct; these are emitted frequently.</summary>
public readonly record struct ScanProgress(
    long DirectoriesScanned,
    long FilesScanned,
    long BytesScanned,
    string CurrentPath);

/// <summary>Everything a completed scan produced.</summary>
public sealed class ScanResult
{
    public required ScanNode Root { get; init; }
    public required TimeSpan Duration { get; init; }

    public long TotalFiles { get; init; }
    public long TotalDirectories { get; init; }
    public long TotalAllocatedBytes { get; init; }
    public long TotalLogicalBytes { get; init; }

    /// <summary>
    /// Directories that could not be opened. Their contents are absent from every total,
    /// which is why this count is surfaced rather than swallowed: a scanner that silently
    /// skips unreadable subtrees reports a number that looks authoritative and is not.
    /// </summary>
    public int AccessDeniedCount { get; init; }
    public int FailedCount { get; init; }

    /// <summary>Junctions and symlinks that were intentionally not followed.</summary>
    public int SkippedSurrogateCount { get; init; }

    /// <summary>Bytes attributed to additional hardlinks and therefore not double-counted.</summary>
    public long HardLinkBytesDeduplicated { get; init; }
    public long HardLinkFilesDeduplicated { get; init; }
}

/// <summary>Scans a directory tree and reports how space is distributed within it.</summary>
public interface IVolumeScanner
{
    ScanResult Scan(ScanOptions options, CancellationToken cancellationToken = default);
}
