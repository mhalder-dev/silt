using System.Text.Json.Serialization;

namespace Silt.Api;

/// <summary>A volume offered to the user for scanning.</summary>
public sealed record VolumeDto(
    string Root,
    string Label,
    string FileSystem,
    long CapacityBytes,
    long FreeBytes,
    bool IsReady);

/// <summary>Handle returned when a scan is started.</summary>
public sealed record ScanHandleDto(string ScanId);

public enum ScanState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Live scan status, polled by the UI while a scan runs.</summary>
public sealed record ScanStatusDto(
    string ScanId,
    ScanState State,
    string Root,
    long DirectoriesScanned,
    long FilesScanned,
    long BytesScanned,
    string CurrentPath,
    double ElapsedSeconds,
    string? Error);

/// <summary>One directory in the tree view.</summary>
public sealed record TreeNodeDto(
    string Name,
    string Path,
    long AllocatedBytes,
    long LogicalBytes,
    long FileCount,
    int DirectoryCount,
    bool HasChildren,
    [property: JsonPropertyName("conditions")] IReadOnlyList<string> Conditions);

/// <summary>A page of children for one directory.</summary>
public sealed record TreeResponseDto(
    string Path,
    long TotalAllocatedBytes,
    IReadOnlyList<TreeNodeDto> Children,
    int TotalChildCount,
    bool Truncated);

public sealed record ReconciliationLineDto(
    string Label,
    long Bytes,
    string Kind,
    string Detail);

public sealed record ReconciliationDto(
    string VolumeRoot,
    long CapacityBytes,
    long FreeBytes,
    long UsedBytes,
    long ScannedBytes,
    long UnaccountedBytes,
    double UnaccountedFraction,
    int InaccessibleDirectoryCount,
    IReadOnlyList<ReconciliationLineDto> Lines);

/// <summary>Everything the UI needs once a scan finishes.</summary>
public sealed record ScanSummaryDto(
    string ScanId,
    string Root,
    double DurationSeconds,
    long TotalFiles,
    long TotalDirectories,
    long TotalAllocatedBytes,
    long TotalLogicalBytes,
    int AccessDeniedCount,
    int FailedCount,
    int SkippedSurrogateCount,
    long HardLinkFilesDeduplicated,
    long HardLinkBytesDeduplicated,
    ReconciliationDto? Reconciliation);

public sealed record AppLocationDto(
    string Path,
    long AllocatedBytes,
    long FileCount,
    string Kind);

public sealed record AppFootprintDto(
    string Key,
    string DisplayName,
    string? Publisher,
    long TotalAllocatedBytes,
    long TotalFileCount,
    bool IsSplitAcrossLocations,
    IReadOnlyList<AppLocationDto> Locations);

public sealed record AppsResponseDto(
    IReadOnlyList<AppFootprintDto> Apps,
    long MinimumBytes,
    long TotalAttributedBytes);

public sealed record DirectoryChangeDto(
    string Path,
    long BeforeBytes,
    long AfterBytes,
    long DeltaBytes,
    long SelfDeltaBytes,
    string Kind);

public sealed record AppChangeDto(
    string Key,
    string DisplayName,
    long BeforeBytes,
    long AfterBytes,
    long DeltaBytes,
    string Kind);

/// <summary>
/// A growth report, or an explanation of why one cannot be produced yet.
/// </summary>
/// <remarks>
/// <c>Available</c> is false on a first scan, when there is nothing to compare against.
/// That is the normal starting state, not an error, so it carries a message rather than a
/// failure status.
/// </remarks>
public sealed record GrowthDto(
    bool Available,
    string? Unavailable,
    DateTimeOffset? FromTakenAt,
    DateTimeOffset? ToTakenAt,
    double SpanDays,
    long FromTotalBytes,
    long ToTotalBytes,
    long DeltaBytes,
    long FreeDeltaBytes,
    bool FloorsDiffer,
    int SnapshotCount,
    IReadOnlyList<DirectoryChangeDto> Directories,
    IReadOnlyList<AppChangeDto> Apps);

public sealed record PlanItemDto(
    string Path, long AllocatedBytes, bool IsDirectory, DateTimeOffset LastWriteUtc);

public sealed record PlanExclusionDto(string Path, string Reason);

public sealed record RulePlanDto(
    string RuleId,
    string DisplayName,
    string Description,
    string Tier,
    string Regeneration,
    string? RegenerationCommand,
    long TotalAllocatedBytes,
    long TotalFileCount,
    int ItemCount,
    int ExclusionCount,
    IReadOnlyList<PlanItemDto> TopItems,
    IReadOnlyList<PlanExclusionDto> SampleExclusions);

public sealed record CleanupPlanDto(
    string PlanId,
    DateTimeOffset CreatedAt,
    long TotalAllocatedBytes,
    long TotalFileCount,
    int TotalItemCount,
    IReadOnlyList<RulePlanDto> Rules);

public sealed record FailedItemDto(string Path, string Reason);

public sealed record ExecutionResultDto(
    string OperationId,
    string RuleId,
    bool Executed,
    string Refusal,
    string? RefusalMessage,
    int ItemsDeleted,
    int ItemsFailed,
    long BytesDeleted,
    long RecycleBinAvailableBytes,
    IReadOnlyList<FailedItemDto> Failures);

public sealed record JournalEntryDto(
    string OperationId,
    DateTimeOffset At,
    string RuleId,
    string Path,
    long Bytes,
    bool Succeeded,
    bool Recoverable,
    string? Failure);

public sealed record JournalDto(
    bool Intact,
    string? FirstBreakAt,
    int TotalEntries,
    IReadOnlyList<JournalEntryDto> Entries);

public sealed record ErrorDto(string Message);
