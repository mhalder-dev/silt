using System.Runtime.InteropServices;
using Silt.Core.Native;
using Silt.Core.Scanning;

namespace Silt.Core.Reconciliation;

/// <summary>One line of the reconciliation waterfall.</summary>
/// <param name="Label">Human-readable name.</param>
/// <param name="Bytes">Bytes attributed to this line.</param>
/// <param name="Kind">How the figure was obtained.</param>
/// <param name="Detail">Why this line exists, in the user's language.</param>
public readonly record struct ReconciliationLine(
    string Label,
    long Bytes,
    ReconciliationKind Kind,
    string Detail);

/// <summary>Provenance of a waterfall line. The UI colours by this.</summary>
public enum ReconciliationKind
{
    /// <summary>Measured directly by the scan.</summary>
    Measured,

    /// <summary>Known to exist and sized, but not part of the scanned tree.</summary>
    Known,

    /// <summary>Known to exist but not measurable without more privilege.</summary>
    Unmeasured,

    /// <summary>The residual. Never silently folded into anything else.</summary>
    Unaccounted,
}

/// <summary>The full picture of where a volume's used bytes are.</summary>
public sealed class VolumeReconciliation
{
    public required string VolumeRoot { get; init; }
    public required long CapacityBytes { get; init; }
    public required long FreeBytes { get; init; }
    public long UsedBytes => CapacityBytes - FreeBytes;

    public required long ScannedBytes { get; init; }
    public required int InaccessibleDirectoryCount { get; init; }
    public required IReadOnlyList<ReconciliationLine> Lines { get; init; }

    /// <summary>
    /// Used bytes the scan could not attribute to anything. Positive means space is
    /// unexplained; negative means the scan over-counted, which is a bug worth surfacing.
    /// </summary>
    public required long UnaccountedBytes { get; init; }

    public double UnaccountedFraction =>
        UsedBytes <= 0 ? 0 : (double)UnaccountedBytes / UsedBytes;
}

/// <summary>
/// Explains the difference between what a scan measured and what the volume reports as used.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the alternative is dishonest. Every folder-size tool shows a total
/// that is smaller than the volume's used space, and most of them simply do not mention it.
/// The gap is real — page file, hibernation file, NTFS metadata, shadow copies, and any
/// subtree the process could not read — and a user hunting for missing gigabytes deserves to
/// see it itemized rather than to wonder whether the tool is lying.
/// </para>
/// <para>
/// The residual is never absorbed into another line to make the arithmetic look tidy.
/// </para>
/// </remarks>
public static class VolumeReconciler
{
    /// <summary>Files that live at a volume root and are not ordinary user data.</summary>
    private static readonly string[] SystemRootFiles =
    [
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "DumpStack.log",
        "DumpStack.log.tmp",
    ];

    public static VolumeReconciliation Reconcile(ScanResult scan, string volumeRoot)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        string root = Path.GetPathRoot(Path.GetFullPath(volumeRoot))
                      ?? throw new ArgumentException("Not a rooted path.", nameof(volumeRoot));

        if (!NativeMethods.GetDiskFreeSpaceEx(root, out _, out ulong capacity, out ulong free))
        {
            throw new InvalidOperationException(
                $"GetDiskFreeSpaceEx failed for '{root}' (Win32 {Marshal.GetLastWin32Error()}).");
        }

        long capacityBytes = (long)capacity;
        long freeBytes = (long)free;
        long usedBytes = capacityBytes - freeBytes;

        long scanned = scan.TotalAllocatedBytes;
        var lines = new List<ReconciliationLine>(6);

        // Split the system files out of the scanned figure so the user sees them named.
        // They sit at the volume root, so they are inside ScannedBytes already; showing them
        // separately would double-count unless they are subtracted from the measured line.
        long systemFileBytes = 0;
        var namedSystemFiles = new List<(string Name, long Bytes)>();

        if (scan.Root.Children is not null || scan.Root.OwnFileCount > 0)
        {
            foreach (string candidate in SystemRootFiles)
            {
                string path = Path.Combine(root, candidate);
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        long len = info.Length;
                        systemFileBytes += len;
                        namedSystemFiles.Add((candidate, len));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Reported as unmeasured rather than assumed absent.
                }
            }
        }

        long userData = scanned - systemFileBytes;

        lines.Add(new ReconciliationLine(
            "Files and folders",
            userData,
            ReconciliationKind.Measured,
            $"{scan.TotalFiles:N0} files across {scan.TotalDirectories:N0} directories, " +
            "counted by allocated size."));

        foreach ((string name, long bytes) in namedSystemFiles.OrderByDescending(f => f.Bytes))
        {
            lines.Add(new ReconciliationLine(
                name,
                bytes,
                ReconciliationKind.Known,
                DescribeSystemFile(name)));
        }

        if (scan.HardLinkBytesDeduplicated > 0)
        {
            lines.Add(new ReconciliationLine(
                "Hardlinked content (counted once)",
                0,
                ReconciliationKind.Known,
                $"{scan.HardLinkFilesDeduplicated:N0} additional links to files already " +
                $"counted. Naively summing them would have added " +
                $"{FormatBytes(scan.HardLinkBytesDeduplicated)} that does not exist on disk."));
        }

        if (scan.AccessDeniedCount > 0)
        {
            lines.Add(new ReconciliationLine(
                "Unreadable directories",
                0,
                ReconciliationKind.Unmeasured,
                $"{scan.AccessDeniedCount:N0} directories could not be opened, so their " +
                "contents are absent from the measured total. Their bytes appear below as " +
                "unaccounted."));
        }

        long accountedFor = lines
            .Where(l => l.Kind is ReconciliationKind.Measured or ReconciliationKind.Known)
            .Sum(l => l.Bytes);

        long unaccounted = usedBytes - accountedFor;

        lines.Add(new ReconciliationLine(
            "Unaccounted",
            unaccounted,
            ReconciliationKind.Unaccounted,
            BuildUnaccountedExplanation(unaccounted, usedBytes, scan)));

        return new VolumeReconciliation
        {
            VolumeRoot = root,
            CapacityBytes = capacityBytes,
            FreeBytes = freeBytes,
            ScannedBytes = scanned,
            InaccessibleDirectoryCount = scan.AccessDeniedCount,
            UnaccountedBytes = unaccounted,
            Lines = lines,
        };
    }

    private static string DescribeSystemFile(string name) => name switch
    {
        "pagefile.sys" =>
            "Virtual memory. Windows manages its size. Disabling it on a machine with " +
            "limited RAM trades disk space for instability.",
        "hiberfil.sys" =>
            "Hibernation image, sized from installed RAM. Reclaimable by disabling " +
            "hibernation, which also turns off Fast Startup.",
        "swapfile.sys" =>
            "Backing store for suspended Store apps. Small, and managed with the page file.",
        _ => "System file at the volume root.",
    };

    private static string BuildUnaccountedExplanation(long unaccounted, long used, ScanResult scan)
    {
        if (unaccounted < 0)
        {
            return "The scan measured MORE than the volume reports as used. That is a bug, " +
                   "not a disk condition - most likely double-counted hardlinks or a " +
                   "traversed junction.";
        }

        double pct = used <= 0 ? 0 : (double)unaccounted / used * 100;
        var causes = new List<string>();

        if (scan.AccessDeniedCount > 0)
        {
            causes.Add($"the {scan.AccessDeniedCount:N0} directories that could not be read");
        }

        causes.Add("NTFS metadata such as the $MFT, which is not reachable as a file");
        causes.Add("Volume Shadow Copy snapshots, which require elevation to enumerate");

        return $"{FormatBytes(unaccounted)} ({pct:F1}% of used space) is not explained by " +
               $"anything above. Likely {string.Join(", ", causes)}. " +
               "This figure is shown rather than absorbed into another line so it can be " +
               "investigated instead of assumed away.";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        int unit = 0;
        while (Math.Abs(v) >= 1024 && unit < units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        return $"{v:F2} {units[unit]}";
    }
}
