namespace Silt.Safety;

/// <summary>
/// Decides whether one path is genuinely inside another.
/// </summary>
/// <remarks>
/// <para>
/// A pure predicate: no I/O, no P/Invoke, no platform calls. That is what allows it to be
/// property-tested exhaustively and to be the single primitive every component relies on
/// instead of each hand-rolling its own containment check.
/// </para>
/// <para>
/// <b>Limitations, stated rather than hidden.</b> Because it never touches the filesystem it
/// cannot resolve symlinks, junctions, or 8.3 short names — <c>C:\PROGRA~1</c> cannot become
/// <c>C:\Program Files</c> without <c>GetLongPathName</c>. Callers that act on the result
/// must therefore also verify the resolved target at the moment they act, on an already-open
/// handle where possible. This type answers "is this path string inside that path string",
/// which is necessary but not sufficient for a destructive operation.
/// </para>
/// </remarks>
public static class PathJail
{
    /// <summary>
    /// True when <paramref name="candidate"/> is the same as, or beneath,
    /// <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Two mistakes this exists to prevent:
    /// <list type="bullet">
    /// <item>
    /// A bare <c>StartsWith</c> lets <c>C:\data-evil</c> pass as inside <c>C:\data</c>.
    /// A separator is required after the root prefix.
    /// </item>
    /// <item>
    /// Comparing before canonicalization lets <c>C:\data\..\windows</c> pass. Both sides are
    /// fully qualified first.
    /// </item>
    /// </list>
    /// </remarks>
    public static bool IsContained(string root, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        string canonicalRoot, canonicalCandidate;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or PathTooLongException)
        {
            // A path that cannot even be canonicalized is not inside anything.
            return false;
        }

        if (string.Equals(canonicalRoot, canonicalCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The separator must not be appended blindly.
        //
        // Path.TrimEndingDirectorySeparator does not trim the separator from a volume root:
        // "C:\" stays "C:\", by design, because trimming would change its meaning. Appending
        // another separator then produces "C:\\", which no real path starts with, so every
        // containment test against a drive root returned false — failing OPEN on the widest
        // possible root. A denylist entry protecting an entire volume protected nothing.
        string prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;

        return canonicalCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Throws unless <paramref name="candidate"/> is inside <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// For call sites that are about to write or delete. Failing loudly is the point: a
    /// containment check whose result can be ignored is decoration.
    /// </remarks>
    public static void Require(string root, string candidate, string operation)
    {
        if (!IsContained(root, candidate))
        {
            throw new UnauthorizedAccessException(
                $"Refusing to {operation}: '{candidate}' is outside '{root}'.");
        }
    }
}
