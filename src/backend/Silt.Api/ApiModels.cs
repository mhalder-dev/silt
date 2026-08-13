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

public sealed record ErrorDto(string Message);
