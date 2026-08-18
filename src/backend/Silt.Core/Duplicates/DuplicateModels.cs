using Silt.Safety;

namespace Silt.Core.Duplicates;

/// <summary>Inputs to a duplicate search.</summary>
public sealed class DuplicateOptions
{
    /// <summary>Directory to search. Everything beneath it is considered.</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Files smaller than this are ignored entirely.
    /// </summary>
    /// <remarks>
    /// Defaults to one 4 KiB cluster. Below that there is nothing to reclaim — a duplicate
    /// 200-byte file frees a cluster at best — while small files are the overwhelming
    /// majority by count. On a developer profile the sub-cluster population is dominated by
    /// <c>node_modules</c> and git objects, which would produce tens of thousands of
    /// "findings" worth a few megabytes in total and bury every real one.
    /// </remarks>
    public long MinimumFileSize { get; init; } = 4096;

    /// <summary>
    /// Confirm each reported duplicate by comparing bytes, not only hashes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to on, and should stay on. A SHA-256 collision is not reachable by accident,
    /// but it <em>is</em> constructible by someone who can write two files onto the volume,
    /// and the consequence here is a tool telling a user that two unrelated files are
    /// interchangeable. Silt's product is the refusal to be confidently wrong about that.
    /// </para>
    /// <para>
    /// The cost is a second sequential read of the confirmed duplicates only — measured in
    /// <c>docs/PLAN.md</c> §5i. Everything culled earlier is never re-read.
    /// </para>
    /// </remarks>
    public bool VerifyByteForByte { get; init; } = true;

    /// <summary>
    /// When set, files this denylist refuses are excluded from the report.
    /// </summary>
    /// <remarks>
    /// A duplicate report exists to lead to a deletion. Listing a file that the safety layer
    /// would refuse to delete anyway is an invitation to go around Silt and delete it in
    /// Explorer instead — which is strictly worse than not reporting it.
    /// </remarks>
    public Denylist? Denylist { get; init; }

    /// <summary>Hashing workers. Defaults to the processor count, clamped.</summary>
    public int DegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <summary>Invoked periodically during hashing. May be called from any thread.</summary>
    public IProgress<DuplicateProgress>? Progress { get; init; }
}

/// <summary>Progress ticket. A struct; these are emitted frequently.</summary>
public readonly record struct DuplicateProgress(
    long FilesHashed,
    long CandidateFiles,
    long BytesRead);

/// <summary>A set of files with identical content.</summary>
public sealed class DuplicateGroup
{
    /// <summary>Logical size of every member, in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Full paths of the members, at least two, ordered shortest-path first.
    /// </summary>
    /// <remarks>
    /// Shortest first because the shortest path is very often the original and the longer
    /// ones the copies (<c>report.pdf</c> versus <c>Downloads\report (2).pdf</c>). Silt does
    /// not act on that guess — it never nominates a member for deletion — but presenting the
    /// likely original first is what makes the group readable at a glance.
    /// </remarks>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>Bytes freed by reducing this group to a single copy.</summary>
    public long ReclaimableBytes => SizeBytes * (Paths.Count - 1);
}

/// <summary>Everything a completed duplicate search produced.</summary>
public sealed class DuplicateResult
{
    /// <summary>Groups, largest reclaimable total first.</summary>
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }

    public required TimeSpan Duration { get; init; }

    public long TotalReclaimableBytes { get; init; }

    /// <summary>Files seen during enumeration, before any filter.</summary>
    public long FilesExamined { get; init; }

    /// <summary>Files that shared a size with at least one other and so had to be read.</summary>
    public long CandidateFiles { get; init; }

    /// <summary>Bytes actually read from disk — the real cost of the search.</summary>
    public long BytesRead { get; init; }

    /// <summary>
    /// Additional hardlinks to a file already counted. They are the same bytes on disk, so
    /// deleting one reclaims nothing; reporting them as duplicates would be a lie.
    /// </summary>
    public long HardLinksCollapsed { get; init; }

    /// <summary>
    /// Cloud-tiered placeholders skipped without being opened. See
    /// <see cref="Native.ReparseTags.IsCloudPlaceholder"/> — reading one downloads it.
    /// </summary>
    public long CloudPlaceholdersSkipped { get; init; }

    /// <summary>Candidates excluded because the denylist refuses them.</summary>
    public long DeniedFilesSkipped { get; init; }

    /// <summary>Directories that could not be opened; their contents are absent.</summary>
    public int AccessDeniedCount { get; init; }

    /// <summary>
    /// Candidates that could not be read — locked, vanished, or failed mid-hash.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than swallowed. A file that failed to hash is silently absent from
    /// every group, so a search reporting zero duplicates over a tree it could not read is
    /// indistinguishable from a clean one unless this number is shown.
    /// </remarks>
    public int UnreadableFileCount { get; init; }
}

/// <summary>Finds sets of files with identical content beneath a directory.</summary>
/// <remarks>
/// An interface for the same reason <see cref="Scanning.IVolumeScanner"/> is one: the API
/// layer must be testable without a real filesystem underneath it.
/// </remarks>
public interface IDuplicateFinder
{
    DuplicateResult Find(DuplicateOptions options, CancellationToken cancellationToken = default);
}
