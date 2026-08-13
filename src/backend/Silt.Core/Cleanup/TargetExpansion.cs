namespace Silt.Core.Cleanup;

/// <summary>
/// Turns a rule's path template into the concrete directories present on this machine.
/// </summary>
/// <remarks>
/// Supports <c>%ENVVAR%</c> and a literal <c>*</c> occupying a whole path segment. The
/// wildcard is what lets one rule address every Chrome profile and every JetBrains product
/// without hardcoding names that differ per machine and appear over time.
/// </remarks>
internal static class TargetExpansion
{
    /// <summary>
    /// Returns every existing directory the template resolves to. A template pointing
    /// somewhere absent yields nothing — a rule targeting software that is not installed is
    /// normal, not an error.
    /// </summary>
    internal static IReadOnlyList<string> Expand(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        string expanded = Environment.ExpandEnvironmentVariables(template);

        // An unresolved %VAR% means the variable does not exist on this machine. Treating
        // the literal text as a path would be nonsense, so the target is simply absent.
        if (expanded.Contains('%', StringComparison.Ordinal))
        {
            return [];
        }

        if (!expanded.Contains('*', StringComparison.Ordinal))
        {
            return Directory.Exists(expanded) ? [expanded] : [];
        }

        string[] segments = expanded.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);

        var current = new List<string>();
        int start = 0;

        // Seed with the rooted prefix before any wildcard.
        string? root = Path.GetPathRoot(expanded);
        if (!string.IsNullOrEmpty(root))
        {
            current.Add(root);
            // Skip the segments consumed by the root ("C:" for "C:\...").
            start = root.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Length;
        }
        else
        {
            return [];
        }

        for (int i = start; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            var next = new List<string>();
            foreach (string parent in current)
            {
                if (segment == "*")
                {
                    IEnumerable<string> children;
                    try
                    {
                        children = Directory.EnumerateDirectories(parent);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    next.AddRange(children);
                }
                else
                {
                    string candidate = Path.Combine(parent, segment);
                    if (Directory.Exists(candidate))
                    {
                        next.Add(candidate);
                    }
                }
            }

            current = next;
            if (current.Count == 0)
            {
                return [];
            }
        }

        return current;
    }
}
