using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Silt.Core.Native;

/// <summary>
/// Win32 entry points used by the scanner.
/// </summary>
/// <remarks>
/// Handles are returned as <see cref="IntPtr"/> and wrapped by the caller rather than being
/// marshalled directly as <see cref="SafeFileHandle"/>: LibraryImport can only marshal a
/// SafeHandle return value when the type exposes a public parameterless constructor, and
/// SafeFileHandle does not.
/// </remarks>
internal static partial class NativeMethods
{
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;
    internal const uint OPEN_EXISTING = 3;

    /// <summary>Required to obtain a handle to a directory rather than a file.</summary>
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary>
    /// Opens the reparse point itself instead of its target. Essential for the scanner:
    /// without it, opening a junction silently follows it and the same subtree is counted
    /// twice (or recurses forever on a cycle).
    /// </summary>
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_NO_MORE_FILES = 18;
    internal const int ERROR_MORE_DATA = 234;

    /// <summary>FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryInfo — continue enumeration.</summary>
    internal const int FileIdBothDirectoryInfo = 10;

    /// <summary>FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryRestartInfo — begin enumeration.</summary>
    internal const int FileIdBothDirectoryRestartInfo = 11;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        IntPtr lpFileInformation,
        uint dwBufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    /// <summary>
    /// Opens a directory for enumeration without following a reparse point and without
    /// touching file contents. Returns an invalid handle on failure; the caller inspects
    /// <see cref="Marshal.GetLastWin32Error"/>.
    /// </summary>
    internal static SafeFileHandle OpenDirectory(string path)
    {
        IntPtr raw = CreateFile(
            path,
            FILE_LIST_DIRECTORY,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        return new SafeFileHandle(raw, ownsHandle: true);
    }
}
