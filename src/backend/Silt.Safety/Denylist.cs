namespace Silt.Safety;

/// <summary>How much of a location is protected.</summary>
public enum ProtectionKind
{
    /// <summary>The path and everything beneath it.</summary>
    Subtree,

    /// <summary>The path itself, but not its contents.</summary>
    ExactOnly,
}

/// <summary>A location the cleanup engine must never touch, and why.</summary>
/// <param name="Reason">
/// Shown to the user verbatim when something is refused, so it has to explain the
/// consequence rather than merely name a rule.
/// </param>
public sealed record ProtectedPath(string Path, string Reason, ProtectionKind Kind = ProtectionKind.Subtree);

/// <summary>The outcome of a denylist check.</summary>
public readonly record struct DenyVerdict(bool IsDenied, string? Reason, string? MatchedRule)
{
    public static DenyVerdict Allowed => new(false, null, null);

    public static DenyVerdict Deny(string reason, string rule) => new(true, reason, rule);
}

/// <summary>
/// The last line of defence: locations and file kinds that are never deletable.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no override.</b> No flag, no force parameter, no "advanced" switch. A rule
/// cannot opt out of it, the UI cannot bypass it, and the wire format has no field that
/// could express a bypass. Every escape hatch that exists will eventually be used by a bug.
/// </para>
/// <para>
/// Protected paths are <b>injected</b> rather than resolved here, which keeps this type pure
/// and testable, and — more importantly — lets the startup canary verify the list using a
/// <em>different</em> path source. A denylist and its self-test that share one resolver
/// agree with each other even when both are wrong, which is precisely the failure that
/// matters: if the process ever resolves a different profile, the list silently protects
/// the wrong Documents folder and a shared-resolver canary passes anyway.
/// </para>
/// </remarks>
public sealed class Denylist
{
    private readonly ProtectedPath[] _paths;

    /// <summary>
    /// Directory names that are never deletable wherever they appear.
    /// </summary>
    /// <remarks>
    /// <c>.git</c> is here because deleting it destroys history that usually exists nowhere
    /// else on the machine, and it is small enough that no cleanup rule should ever want it.
    /// </remarks>
    private static readonly string[] ForbiddenDirectoryNames =
    [
        ".git", ".hg", ".svn", "System Volume Information", "$Recycle.Bin", "WinSxS",
    ];

    /// <summary>
    /// File extensions that are never deletable: secrets, keys, and project definitions.
    /// </summary>
    private static readonly string[] ForbiddenExtensions =
    [
        ".pfx", ".p12", ".key", ".pem", ".ppk", ".kdbx", ".jks", ".keystore",
        ".sln", ".csproj", ".fsproj", ".vbproj", ".slnx",
    ];

    /// <summary>
    /// File names that are never deletable. Matched case-insensitively against the whole
    /// name, or as a prefix where a suffix is conventional (appsettings.Production.json).
    /// </summary>
    private static readonly string[] ForbiddenNamePrefixes =
    [
        "appsettings", "secrets.json", ".env", "id_rsa", "id_ed25519", "id_ecdsa",
        "credentials", ".npmrc", ".pgpass", "NuGet.Config",
    ];

    public Denylist(IEnumerable<ProtectedPath> protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(protectedPaths);
        _paths = [.. protectedPaths];
    }

    /// <summary>Every protected location, for the startup canary to assert against.</summary>
    public IReadOnlyList<ProtectedPath> ProtectedPaths => _paths;

    /// <summary>
    /// Decides whether <paramref name="candidate"/> may be deleted.
    /// </summary>
    /// <remarks>
    /// Fails closed: anything that cannot be canonicalized is denied rather than allowed
    /// through on the assumption it is harmless.
    /// </remarks>
    public DenyVerdict Check(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return DenyVerdict.Deny("An empty path cannot be deleted.", "empty-path");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or PathTooLongException)
        {
            return DenyVerdict.Deny(
                "This path could not be interpreted, so it is refused rather than guessed at.",
                "unparseable-path");
        }

        // A volume root is never a deletion target, whatever a rule says.
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return DenyVerdict.Deny("This is the root of a volume.", "volume-root");
        }

        foreach (ProtectedPath protectedPath in _paths)
        {
            bool hit = protectedPath.Kind == ProtectionKind.Subtree
                ? PathJail.IsContained(protectedPath.Path, full)
                : string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath.Path)),
                    full,
                    StringComparison.OrdinalIgnoreCase);

            if (hit)
            {
                return DenyVerdict.Deny(protectedPath.Reason, protectedPath.Path);
            }
        }

        foreach (string segment in full.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string forbidden in ForbiddenDirectoryNames)
            {
                if (string.Equals(segment, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    return DenyVerdict.Deny(
                        $"'{forbidden}' holds data that usually exists nowhere else.",
                        $"dir:{forbidden}");
                }
            }
        }

        string name = Path.GetFileName(full);
        if (name.Length > 0)
        {
            string extension = Path.GetExtension(name);
            foreach (string forbidden in ForbiddenExtensions)
            {
                if (string.Equals(extension, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    return DenyVerdict.Deny(
                        $"'{forbidden}' files hold keys or project definitions.",
                        $"ext:{forbidden}");
                }
            }

            foreach (string prefix in ForbiddenNamePrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return DenyVerdict.Deny(
                        $"Files named like '{prefix}' hold configuration or secrets.",
                        $"name:{prefix}");
                }
            }
        }

        return DenyVerdict.Allowed;
    }
}
