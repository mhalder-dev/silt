using System.Runtime.InteropServices;

namespace Silt.Core.Native;

/// <summary>
/// Native <c>FILE_ID_BOTH_DIR_INFO</c>, as returned by
/// <c>GetFileInformationByHandleEx(FileIdBothDirectoryInfo)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Offsets are declared explicitly rather than left to sequential layout, because the
/// structure contains two natural alignment gaps that are easy to get wrong: a 1-byte
/// <c>ShortNameLength</c> followed by a 2-byte-aligned <c>ShortName</c>, and a 24-byte
/// <c>ShortName</c> followed by an 8-byte-aligned <c>FileId</c>. A silently wrong offset
/// here does not crash — it yields plausible-looking garbage sizes.
/// <c>FileIdBothDirInfoLayoutTests</c> asserts every offset.
/// </para>
/// <para>
/// <c>EaSize</c> is overloaded by the filesystem: when
/// <c>FILE_ATTRIBUTE_REPARSE_POINT</c> is set in <c>FileAttributes</c>, this field carries
/// the reparse tag instead of an extended-attribute size. That is the only way to obtain
/// the tag from a directory enumeration, and it is why the scanner can classify junctions
/// without a second open per entry.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit)]
internal struct FileIdBothDirInfo
{
    [FieldOffset(0)] internal uint NextEntryOffset;
    [FieldOffset(4)] internal uint FileIndex;
    [FieldOffset(8)] internal long CreationTime;
    [FieldOffset(16)] internal long LastAccessTime;
    [FieldOffset(24)] internal long LastWriteTime;
    [FieldOffset(32)] internal long ChangeTime;

    /// <summary>Logical file size in bytes.</summary>
    [FieldOffset(40)] internal long EndOfFile;

    /// <summary>
    /// Bytes actually allocated on disk. This is the figure the scanner reports by default:
    /// it is what the volume's free space responds to. Logical size overstates sparse and
    /// NTFS-compressed files, sometimes by orders of magnitude.
    /// </summary>
    [FieldOffset(48)] internal long AllocationSize;

    [FieldOffset(56)] internal uint FileAttributes;
    [FieldOffset(60)] internal uint FileNameLength;

    /// <summary>EA size, or the reparse tag when the reparse-point attribute is set.</summary>
    [FieldOffset(64)] internal uint EaSize;

    [FieldOffset(68)] internal sbyte ShortNameLength;

    // ShortName: WCHAR[12] at offset 70 (2-byte aligned after the 1-byte length + 1 pad).
    // Not surfaced - the scanner has no use for 8.3 names, and resolving them would require
    // a filesystem round trip the path jail deliberately refuses to make.

    /// <summary>
    /// 64-bit file identity, unique within the volume. Combined with the volume serial this
    /// is what makes hardlink de-duplication possible; without it, WinSxS over-reports by
    /// roughly 2x.
    /// </summary>
    [FieldOffset(96)] internal long FileId;

    /// <summary>Offset of the variable-length UTF-16 filename. Not null-terminated.</summary>
    internal const int FileNameOffset = 104;

    /// <summary>Header size excluding the variable-length name.</summary>
    internal const int HeaderSize = FileNameOffset;

    internal readonly bool IsDirectory =>
        (FileAttributes & (uint)FileAttributes_.Directory) != 0;

    internal readonly bool IsReparsePoint =>
        (FileAttributes & (uint)FileAttributes_.ReparsePoint) != 0;

    /// <summary>The reparse tag, or 0 when this entry is not a reparse point.</summary>
    internal readonly uint ReparseTag => IsReparsePoint ? EaSize : 0u;
}

/// <summary>
/// Subset of Win32 file attributes used by the scanner. Named with a trailing underscore to
/// avoid colliding with <see cref="System.IO.FileAttributes"/> at the call site.
/// </summary>
[Flags]
internal enum FileAttributes_ : uint
{
    Directory = 0x0000_0010,
    ReparsePoint = 0x0000_0400,
    Offline = 0x0000_1000,

    /// <summary>Cloud-tiered content; reading it would trigger a download.</summary>
    RecallOnDataAccess = 0x0040_0000,
    RecallOnOpen = 0x0004_0000,
}
