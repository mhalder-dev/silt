using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Silt.Core.Cleanup;

/// <summary>What the Recycle Bin currently holds and how much it will accept.</summary>
public sealed record RecycleBinState(
    string VolumeRoot,
    long CurrentBytes,
    long CurrentItems,
    long MaxCapacityBytes,
    bool CapacityKnown)
{
    /// <summary>Bytes that can still be recycled before the bin starts evicting.</summary>
    public long AvailableBytes => Math.Max(0, MaxCapacityBytes - CurrentBytes);
}

/// <summary>Reports Recycle Bin quota for a volume.</summary>
/// <remarks>
/// An interface so the executor's refusal behaviour can be tested without needing a real
/// bin that is nearly full. Refusal is the executor's most important property; it cannot be
/// left to whatever state the developer's machine happens to be in.
/// </remarks>
public interface IRecycleBinProbe
{
    RecycleBinState Query(string volumeRoot);
}

/// <summary>Queries the real Recycle Bin.</summary>
public sealed class RecycleBinProbe : IRecycleBinProbe
{
    public RecycleBinState Query(string volumeRoot) => RecycleBinCapacity.Query(volumeRoot);
}

/// <summary>
/// Reads Recycle Bin quota. Read-only — nothing here mutates anything.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the Recycle Bin is a quota, not a guarantee. When a delete exceeds
/// the bin's capacity the shell does not fail — it <b>permanently destroys the file</b> and
/// reports success. On the development machine the quota is 23,826 MB against a flagship
/// operation that was once 44 GB.
/// </para>
/// <para>
/// Silt therefore checks capacity <em>before</em> executing and refuses a batch that will
/// not fit, rather than discovering afterwards that recovery is impossible. Reporting
/// "restore_possible = false" after the fact is honest reporting of data loss, not
/// prevention.
/// </para>
/// </remarks>
public static partial class RecycleBinCapacity
{
    /// <summary>
    /// Native <c>SHQUERYRBINFO</c>.
    /// </summary>
    /// <remarks>
    /// Natural alignment, not <c>Pack = 1</c>. Packed to 1 byte the struct measures 20 bytes
    /// instead of 24, so <c>cbSize</c> is wrong and <c>SHQueryRecycleBin</c> rejects the
    /// call. The failure is silent in the worst way: the query returns an error, the counts
    /// stay zero, and the bin appears permanently empty — which made the capacity guard
    /// believe the whole quota was free no matter what the bin actually held.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    public static RecycleBinState Query(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        string root = Path.GetPathRoot(Path.GetFullPath(volumeRoot))
                      ?? throw new ArgumentException("Not a rooted path.", nameof(volumeRoot));

        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        long currentBytes = 0;
        long currentItems = 0;
        bool queried = SHQueryRecycleBin(root, ref info) == 0;

        if (queried)
        {
            currentBytes = info.i64Size;
            currentItems = info.i64NumItems;
        }

        (long max, bool known) = ReadMaxCapacity(root);

        // If the bin's current contents cannot be read, the free capacity is unknown even
        // when the quota is. Reporting the full quota as available would authorize a batch
        // that overflows an already-full bin, destroying the overflow.
        return new RecycleBinState(root, currentBytes, currentItems, max, known && queried);
    }

    /// <summary>
    /// Reads the configured per-volume quota from the registry.
    /// </summary>
    /// <remarks>
    /// Windows stores the quota under the volume's GUID in
    /// <c>HKCU\...\Explorer\BitBucket\Volume\{guid}\MaxCapacity</c>, in megabytes. There is
    /// no supported API for it. When it cannot be read, the caller is told the capacity is
    /// unknown rather than being handed a guess that could authorize an unrecoverable
    /// deletion.
    /// </remarks>
    private static (long MaxBytes, bool Known) ReadMaxCapacity(string root)
    {
        try
        {
            using RegistryKey? bucket = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume");

            if (bucket is not null)
            {
                foreach (string volumeGuid in bucket.GetSubKeyNames())
                {
                    using RegistryKey? volume = bucket.OpenSubKey(volumeGuid);
                    if (volume?.GetValue("MaxCapacity") is not int megabytes)
                    {
                        continue;
                    }

                    // A volume GUID cannot be mapped back to a drive letter without more
                    // interop than this is worth, so the smallest configured quota is used.
                    // Erring low means a batch is refused that might have fitted, which is
                    // the harmless direction to be wrong in.
                    return ((long)megabytes * 1024 * 1024, true);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                      or UnauthorizedAccessException or IOException)
        {
        }

        return (0, false);
    }
}
