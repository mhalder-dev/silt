using System.Runtime.InteropServices;
using Silt.Safety;

namespace Silt.Core.Cleanup;

/// <summary>What happened to one item.</summary>
public sealed record DeletionOutcome(
    string Path,
    bool Succeeded,
    bool WentToRecycleBin,
    string? Failure);

/// <summary>
/// The only code in Silt permitted to remove anything from disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the funnel.</b> CI enforces that no other source file references a filesystem
/// mutation API, so every deletion in the product passes through here and inherits the same
/// checks. The Win32 interop lives in this file rather than in a shared natives file
/// specifically so the boundary is literal: there is no mutation primitive available
/// elsewhere to call.
/// </para>
/// <para>
/// Three guarantees, enforced here rather than trusted from the caller:
/// </para>
/// <list type="number">
/// <item>
/// <b>The denylist is re-checked at execution time.</b> The planner already checked, but a
/// plan is a description made earlier; between planning and executing, a path can be
/// replaced by a junction pointing somewhere protected. Checking once is checking at the
/// wrong time.
/// </item>
/// <item>
/// <b>Items are re-validated against what the plan recorded.</b> If a file changed size or
/// timestamp since it was planned, it is no longer the thing the user reviewed, and it is
/// skipped rather than deleted on the assumption nothing moved.
/// </item>
/// <item>
/// <b>Deletion goes to the Recycle Bin.</b> There is no permanent-delete path in this type
/// at all. Not a flag, not an overload — the capability does not exist.
/// </item>
/// </list>
/// </remarks>
public sealed partial class SandboxedFileSystem(Denylist denylist)
{
    private readonly Denylist _denylist = denylist ?? throw new ArgumentNullException(nameof(denylist));

    private const uint FO_DELETE = 0x0003;

    /// <summary>Send to the Recycle Bin instead of destroying the file.</summary>
    private const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>
    /// Suppress the shell's per-item confirmation dialogs. Necessary: without it the shell
    /// raises modal prompts mid-operation, which for a batch of a million temp files is
    /// unusable and would drive a developer to strip the flag entirely and lose the
    /// reasoning behind it.
    /// </summary>
    private const ushort FOF_NOCONFIRMATION = 0x0010;

    /// <summary>
    /// Still warn when something cannot be recycled and would be destroyed, even though
    /// confirmations are otherwise suppressed. This is the flag that makes suppressing
    /// confirmations safe rather than reckless.
    /// </summary>
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    /// <summary>
    /// Native <c>SHFILEOPSTRUCTW</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Natural alignment, not <c>Pack = 1</c>.</b> Packing this to 1 byte — which many
    /// published P/Invoke signatures still do, because it was correct for x86 — misaligns
    /// <c>pFrom</c> and every pointer after it on x64. The shell then dereferences a
    /// misaligned pointer and the process dies with an access violation inside
    /// <c>SHFileOperation</c>. Observed exactly that (0xC0000005) before this was corrected.
    /// </para>
    /// <para>
    /// Blittable by design: <c>fAnyOperationsAborted</c> is an <c>int</c> rather than a
    /// marshalled <c>bool</c>, because LibraryImport's source-generated marshalling refuses
    /// non-blittable structs and falling back to the legacy runtime marshaller would be a
    /// step backwards for the one call in the product that deletes files.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW", SetLastError = true)]
    private static partial int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>
    /// Sends the given paths to the Recycle Bin.
    /// </summary>
    /// <param name="validate">
    /// Called per item immediately before deletion, to confirm it still matches what the
    /// plan recorded. Returning a message rejects the item.
    /// </param>
    public IReadOnlyList<DeletionOutcome> RecycleAll(
        IReadOnlyList<string> paths,
        Func<string, string?> validate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(validate);

        var outcomes = new List<DeletionOutcome>(paths.Count);
        var accepted = new List<string>(paths.Count);

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-checked here, not trusted from the plan. Between planning and now, a
            // directory in this path could have been swapped for a junction into somewhere
            // protected.
            DenyVerdict verdict = _denylist.Check(path);
            if (verdict.IsDenied)
            {
                outcomes.Add(new DeletionOutcome(path, false, false,
                    $"Refused by the safety list: {verdict.Reason}"));
                continue;
            }

            if (!Path.IsPathFullyQualified(path))
            {
                outcomes.Add(new DeletionOutcome(path, false, false,
                    "Only fully qualified paths can be deleted."));
                continue;
            }

            if (validate(path) is { } rejection)
            {
                outcomes.Add(new DeletionOutcome(path, false, false, rejection));
                continue;
            }

            accepted.Add(path);
        }

        if (accepted.Count == 0)
        {
            return outcomes;
        }

        (bool ok, string? error) = Recycle(accepted);

        foreach (string path in accepted)
        {
            // Ground truth rather than the shell's return code alone: an item that no longer
            // exists was removed, whatever the API reported.
            bool gone = !File.Exists(path) && !Directory.Exists(path);
            outcomes.Add(new DeletionOutcome(
                path,
                gone,
                gone,
                gone ? null : error ?? "The item still exists after the operation."));
        }

        return outcomes;
    }

    /// <summary>
    /// Performs the shell delete. One call for the whole batch: per-item calls are an order
    /// of magnitude slower against a large temp directory.
    /// </summary>
    private static (bool Ok, string? Error) Recycle(IReadOnlyList<string> paths)
    {
        // SHFileOperation takes a double-null-terminated, null-separated list.
        string joined = string.Join('\0', paths) + "\0\0";
        IntPtr from = Marshal.StringToHGlobalUni(joined);

        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING
                         | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMMKDIR,
            };

            int result = SHFileOperation(ref op);

            if (result != 0)
            {
                return (false, $"The shell reported error 0x{result:X} while recycling.");
            }
            if (op.fAnyOperationsAborted != 0)
            {
                return (false, "The operation was aborted before completing.");
            }

            return (true, null);
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
