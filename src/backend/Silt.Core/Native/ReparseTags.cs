namespace Silt.Core.Native;

/// <summary>
/// Reparse-point tag classification.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because the obvious test is wrong in a way that silently corrupts
/// results. Skipping every directory carrying <c>FILE_ATTRIBUTE_REPARSE_POINT</c> also skips
/// OneDrive cloud-backed folders and WOF-compressed folders, which are not redirections at
/// all — their contents live under that path and must be counted. On a typical profile that
/// erases entire subtrees from the report with no error and no warning.
/// </para>
/// <para>
/// The correct predicate is the <em>name surrogate</em> bit. A tag with bit 0x20000000 set
/// means "this name stands in for another name" — junctions and symlinks. Those must not be
/// traversed, or the same bytes are counted twice and a cycle recurses forever. Everything
/// else is a real directory that happens to carry a tag.
/// </para>
/// </remarks>
internal static class ReparseTags
{
    /// <summary>
    /// <c>IsReparseTagNameSurrogate</c> from ntifs.h. Set when the tag denotes a link to
    /// another named entity rather than storage attached to this name.
    /// </summary>
    private const uint NameSurrogateBit = 0x2000_0000;

    internal const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA000_0003; // junction
    internal const uint IO_REPARSE_TAG_SYMLINK = 0xA000_000C;
    internal const uint IO_REPARSE_TAG_WOF = 0x8000_0017;         // compressed, NOT a link
    internal const uint IO_REPARSE_TAG_CLOUD = 0x9000_001A;       // OneDrive, NOT a link

    /// <summary>
    /// True when the tag redirects to another location and the scanner must not descend.
    /// </summary>
    internal static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;

    /// <summary>
    /// True for cloud-tiered placeholders whose content is not resident on this volume.
    /// </summary>
    /// <remarks>
    /// These must never be opened for content. Doing so triggers hydration — silently
    /// downloading the file — which would turn a disk-usage report into an unbounded
    /// download. The scanner only ever reads directory metadata, so this is informational,
    /// but any future code that opens files must honour it.
    /// </remarks>
    internal static bool IsCloudPlaceholder(uint tag) =>
        tag is 0x9000_001A or 0x9000_101A or 0x9000_201A or 0x9000_301A or 0x9000_401A;
}
