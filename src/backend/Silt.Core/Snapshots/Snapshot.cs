namespace Silt.Core.Snapshots;

/// <summary>One directory as recorded in a snapshot.</summary>
public sealed record SnapshotEntry(string Path, long AllocatedBytes, long FileCount);

/// <summary>One application's total as recorded in a snapshot.</summary>
public sealed record SnapshotApp(string Key, string DisplayName, long AllocatedBytes);

/// <summary>
/// A point-in-time record of how a volume's space was distributed.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a full copy of the scan tree. A whole-volume scan holds ~155,000
/// directories; keeping every one of them for every scan would put the history in the same
/// class of disk consumer as the things it is meant to expose. Only directories at or above
/// a size floor are kept, plus everything shallow enough to matter structurally, which cuts
/// a snapshot to a few thousand rows.
/// </para>
/// <para>
/// The floor is recorded in the snapshot itself, because a diff between snapshots taken
/// with different floors would otherwise silently report a directory as "new" when it had
/// merely crossed the threshold.
/// </para>
/// </remarks>
public sealed record Snapshot(
    string Id,
    DateTimeOffset TakenAt,
    string VolumeRoot,
    long CapacityBytes,
    long FreeBytes,
    long TotalAllocatedBytes,
    long TotalFiles,
    long TotalDirectories,
    long EntryFloorBytes,
    IReadOnlyList<SnapshotEntry> Directories,
    IReadOnlyList<SnapshotApp> Apps)
{
    public long UsedBytes => CapacityBytes - FreeBytes;
}

/// <summary>Lightweight header for listing history without loading every snapshot.</summary>
public sealed record SnapshotInfo(
    string Id,
    DateTimeOffset TakenAt,
    string VolumeRoot,
    long TotalAllocatedBytes,
    long FreeBytes);
