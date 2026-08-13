using System.Text;

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
    /// <summary>
    /// This directory's own name. For the scan root this is the full root path
    /// (for example <c>C:\</c>); for every other node it is a single path segment.
    /// </summary>
    public required string Name { get; init; }

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

    /// <summary>
    /// Child directories, or null for a leaf.
    /// </summary>
    /// <remarks>
    /// An array rather than a <c>List&lt;T&gt;</c>: the child count is known exactly when
    /// the directory finishes enumerating and never changes afterwards, so the list wrapper
    /// would be about 32 bytes of pure overhead on every one of a hundred thousand nodes.
    /// </remarks>
    public ScanNode[]? Children { get; set; }

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>
    /// Reconstructs the full path by walking the parent chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is deliberately NOT stored on the node. Measured on a 155,311-directory
    /// scan of C:, keeping a full path string per node cost about 200 bytes each — the
    /// single largest component of the retained tree, and entirely redundant, since the
    /// parent chain already encodes every segment.
    /// </para>
    /// <para>
    /// Call it for rows the user can actually see (a page of a few hundred) rather than for
    /// every node in a traversal.
    /// </para>
    /// </remarks>
    public string BuildPath()
    {
        if (Parent is null)
        {
            return Name;
        }

        // Depth is small in practice; node_modules at 30+ levels is the extreme.
        var segments = new List<string>(12);
        ScanNode node = this;
        while (node.Parent is not null)
        {
            segments.Add(node.Name);
            node = node.Parent;
        }

        var builder = new StringBuilder(node.Name, 160);
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            if (builder.Length > 0 && builder[^1] != Path.DirectorySeparatorChar)
            {
                builder.Append(Path.DirectorySeparatorChar);
            }
            builder.Append(segments[i]);
        }

        return builder.ToString();
    }

    public override string ToString() => $"{Name} ({TotalAllocatedBytes:N0} bytes)";
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
