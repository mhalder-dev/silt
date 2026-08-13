using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Silt.Core.Native;

namespace Silt.Core.Scanning;

/// <summary>Outcome of enumerating a single directory.</summary>
internal enum EnumerateStatus
{
    Ok,
    AccessDenied,
    NotFound,
    /// <summary>Any other Win32 failure; the code is reported alongside.</summary>
    Failed,
}

/// <summary>Receives entries as they are read out of the native buffer.</summary>
/// <remarks>
/// Implemented as a generic constraint rather than an interface reference so the JIT can
/// devirtualize the callback. At roughly a million calls per scan, a virtual dispatch per
/// entry is measurable.
/// </remarks>
internal interface IEntrySink
{
    void OnEntry(ReadOnlySpan<char> name, in FileIdBothDirInfo info);
}

/// <summary>
/// Enumerates a directory via <c>GetFileInformationByHandleEx</c>.
/// </summary>
/// <remarks>
/// <para>
/// .NET's <c>FileSystemEnumerator</c> is faster to write and was the obvious choice, but its
/// <c>FileSystemEntry</c> exposes no file identity of any kind — the native fill is
/// <c>FILE_FULL_DIR_INFORMATION</c>, which carries no file ID. Without identity there is no
/// hardlink de-duplication, and without that WinSxS is over-reported by roughly 2x.
/// It also cannot surface the reparse tag, only the attribute bit, which is not enough to
/// distinguish a junction from a OneDrive folder.
/// </para>
/// <para>
/// So the enumeration is hand-rolled on <c>FileIdBothDirectoryInfo</c>, which supplies file
/// id, allocation size, and (via the overloaded <c>EaSize</c> field) the reparse tag in a
/// single pass with no extra open per entry.
/// </para>
/// </remarks>
internal static class DirectoryEnumerator
{
    /// <summary>
    /// Buffer for one <c>GetFileInformationByHandleEx</c> batch. 64 KiB holds several
    /// hundred entries, so even large directories complete in a handful of syscalls.
    /// </summary>
    internal const int BufferSize = 64 * 1024;

    internal static unsafe EnumerateStatus Enumerate<TSink>(
        string directoryPath,
        byte* buffer,
        ref TSink sink,
        out int win32Error)
        where TSink : IEntrySink
    {
        win32Error = 0;

        using SafeFileHandle handle = NativeMethods.OpenDirectory(directoryPath);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            return win32Error switch
            {
                NativeMethods.ERROR_ACCESS_DENIED => EnumerateStatus.AccessDenied,
                NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND
                    => EnumerateStatus.NotFound,
                _ => EnumerateStatus.Failed,
            };
        }

        int infoClass = NativeMethods.FileIdBothDirectoryRestartInfo;

        while (true)
        {
            bool ok = NativeMethods.GetFileInformationByHandleEx(
                handle, infoClass, (IntPtr)buffer, BufferSize);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == NativeMethods.ERROR_NO_MORE_FILES)
                {
                    return EnumerateStatus.Ok;
                }

                win32Error = err;
                return err == NativeMethods.ERROR_ACCESS_DENIED
                    ? EnumerateStatus.AccessDenied
                    : EnumerateStatus.Failed;
            }

            // Subsequent calls must continue rather than restart, or this loops forever.
            infoClass = NativeMethods.FileIdBothDirectoryInfo;

            byte* current = buffer;
            while (true)
            {
                ref FileIdBothDirInfo info = ref *(FileIdBothDirInfo*)current;

                // FileNameLength is a BYTE count, not a character count.
                int nameChars = (int)(info.FileNameLength / sizeof(char));
                var name = new ReadOnlySpan<char>(
                    current + FileIdBothDirInfo.FileNameOffset, nameChars);

                if (!IsDotOrDotDot(name))
                {
                    sink.OnEntry(name, in info);
                }

                if (info.NextEntryOffset == 0)
                {
                    break;
                }

                current += info.NextEntryOffset;
            }
        }
    }

    private static bool IsDotOrDotDot(ReadOnlySpan<char> name) => name.Length switch
    {
        1 => name[0] == '.',
        2 => name[0] == '.' && name[1] == '.',
        _ => false,
    };
}
