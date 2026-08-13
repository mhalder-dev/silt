namespace Silt.Core.Scanning;

/// <summary>Navigation helpers over a completed scan tree.</summary>
public static class ScanTree
{
    /// <summary>
    /// Finds the node for an absolute path, or null if it is outside the scan or was never
    /// reached.
    /// </summary>
    /// <remarks>
    /// Matches segment by segment against names the scanner itself produced rather than
    /// doing a string prefix test on whole paths. A prefix test would resolve
    /// <c>C:\Users\Bob2</c> underneath <c>C:\Users\Bob</c>.
    /// </remarks>
    public static ScanNode? Find(ScanNode root, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));

        // The root node's Name is its full path; descendants hold a bare segment.
        string rootPath = Path.TrimEndingDirectorySeparator(root.Name);

        if (string.Equals(target, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ScanNode current = root;
        foreach (string segment in target[rootPath.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            ScanNode? next = null;
            foreach (ScanNode child in current.Children ?? [])
            {
                if (string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }
            current = next;
        }

        return current;
    }

    /// <summary>Immediate children of the node at <paramref name="absolutePath"/>.</summary>
    public static IReadOnlyList<ScanNode> ChildrenOf(ScanNode root, string absolutePath) =>
        Find(root, absolutePath)?.Children ?? [];
}
