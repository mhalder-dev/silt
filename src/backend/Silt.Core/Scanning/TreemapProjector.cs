namespace Silt.Core.Scanning;

/// <summary>What a treemap rectangle represents.</summary>
public enum TreemapNodeKind
{
    /// <summary>A real directory.</summary>
    Directory,

    /// <summary>
    /// The files sitting directly in a directory, as opposed to in its subdirectories.
    /// </summary>
    /// <remarks>
    /// Synthetic, and not optional. A directory's total is its own files plus its
    /// subdirectories; without this node the loose files have no rectangle and the
    /// subdirectories silently expand to fill space that is not theirs.
    /// </remarks>
    Files,

    /// <summary>Everything under one parent that was too small, or too late, to resolve.</summary>
    /// <remarks>
    /// Aggregated rather than dropped so that the children of an expanded node always sum to
    /// exactly the node's own size. A treemap that quietly discards area is a treemap that
    /// lies about proportion, which is the only thing it exists to convey.
    /// </remarks>
    Other,
}

/// <summary>One rectangle-to-be, in a flattened projection.</summary>
/// <param name="ParentIndex">
/// Index of the parent within the same list, or -1 for the view root. Parents always appear
/// before their children, so a single forward pass can lay the whole thing out.
/// </param>
/// <param name="Name">
/// The view root carries its full path; every other node carries a single path segment. This
/// mirrors <see cref="ScanNode"/> so the renderer can rebuild a full path by walking parents.
/// </param>
/// <param name="Expandable">
/// True when this directory has children that were not projected — the renderer can offer to
/// zoom in rather than implying the folder is empty.
/// </param>
public sealed record TreemapNode(
    int ParentIndex,
    string Name,
    long Bytes,
    TreemapNodeKind Kind,
    bool Expandable,
    NodeCondition Condition);

/// <summary>A flattened subtree, sized to be drawable and to fit in a bounded payload.</summary>
public sealed record TreemapProjection(
    IReadOnlyList<TreemapNode> Nodes,
    long TotalBytes,
    long MinimumBytes,
    int AggregatedNodeCount,
    bool Truncated);

/// <summary>How much of a subtree to project.</summary>
public sealed class TreemapOptions
{
    /// <summary>
    /// Smallest share of the view's total that earns its own rectangle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The renderer culls anything below about 9 px², below which a rectangle cannot be
    /// distinguished, let alone clicked. This is that same rule expressed where the data is:
    /// on a 1200x700 viewport, 9 px² is 1.07e-5 of the drawing area, so a node holding less
    /// than 1e-5 of the total can never survive the pixel cull anyway. Sending it would cost
    /// payload to draw nothing.
    /// </para>
    /// <para>
    /// It is deliberately a fraction and not a byte count: the same rule then holds whether
    /// the view is a 400 GB volume or a 4 MB folder.
    /// </para>
    /// </remarks>
    public double MinimumFraction { get; init; } = 1e-5;

    /// <summary>Hard ceiling on projected nodes.</summary>
    /// <remarks>
    /// A few thousand rectangles is already more than a screen can show. The cap exists so a
    /// pathological tree cannot turn a view into a hundred-thousand-element layout pass.
    /// </remarks>
    public int MaximumNodes { get; init; } = 20_000;

    /// <summary>Ceiling on the estimated serialized payload, in bytes.</summary>
    /// <remarks>
    /// The plan's 8 MB renderer budget. Enforced on an estimate during projection and
    /// asserted against a real serialization in the tests, because an estimate that has never
    /// been checked against the encoder is a guess wearing a number.
    /// </remarks>
    public int MaximumPayloadBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Levels below the view root to resolve.</summary>
    /// <remarks>
    /// Depth is bounded as well as size, because a deep chain of single-child directories
    /// (<c>node_modules</c> reaches 30+) would otherwise consume the node budget producing
    /// rectangles that are all exactly the same size as their parent.
    /// </remarks>
    public int MaximumDepth { get; init; } = 8;
}

/// <summary>
/// Flattens part of a scan tree into the bounded, drawable form the treemap consumes.
/// </summary>
/// <remarks>
/// <para>
/// The backend owns the tree; the renderer never receives it. A whole-C: scan is ~155,000
/// directories, and shipping that to a canvas that can distinguish a few thousand rectangles
/// would cost tens of megabytes to draw nothing extra.
/// </para>
/// <para>
/// Expansion is largest-first rather than breadth-first. Under a node budget, breadth-first
/// spends the budget on whatever happens to be shallow; largest-first spends it on whatever
/// is actually taking up space, which is the question being asked.
/// </para>
/// </remarks>
public static class TreemapProjector
{
    /// <summary>Rough per-node cost of the JSON encoding, excluding the name.</summary>
    /// <remarks>
    /// Field names, punctuation, the numeric byte count and the two enum-ish fields. Measured
    /// against the real serializer by <c>Projection_StaysWithinTheEightMegabytePayloadCap</c>;
    /// deliberately generous, since overestimating truncates a little early while
    /// underestimating breaks the cap the renderer is relying on.
    /// </remarks>
    private const int PerNodeOverheadBytes = 96;

    public static TreemapProjection Project(ScanNode root, TreemapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        TreemapOptions opts = options ?? new TreemapOptions();

        long total = root.TotalAllocatedBytes;
        long minimumBytes = (long)(total * opts.MinimumFraction);

        var nodes = new List<TreemapNode>(Math.Min(opts.MaximumNodes, 1024))
        {
            new(-1, root.Name, total, TreemapNodeKind.Directory,
                Expandable: false, root.Condition),
        };

        int payload = EstimateCost(root.Name);
        int aggregated = 0;
        bool truncated = false;

        // Largest-first. The priority queue is min-first, so sizes are negated.
        var frontier = new PriorityQueue<PendingNode, long>();
        if (root.Children is { Length: > 0 })
        {
            frontier.Enqueue(new PendingNode(root, SelfIndex: 0, Depth: 0), -total);
        }

        while (frontier.TryDequeue(out PendingNode pending, out _))
        {
            ScanNode node = pending.Node;
            long unresolved = 0;
            int unresolvedCount = 0;

            // Loose files first: they are part of this directory's total and must not be
            // crowded out of the budget by its subdirectories.
            if (node.OwnAllocatedBytes > 0)
            {
                if (node.OwnAllocatedBytes >= minimumBytes &&
                    CanAdd(nodes.Count, payload, FilesNodeName, opts))
                {
                    nodes.Add(new TreemapNode(
                        pending.SelfIndex, FilesNodeName, node.OwnAllocatedBytes,
                        TreemapNodeKind.Files, Expandable: false, NodeCondition.None));
                    payload += EstimateCost(FilesNodeName);
                }
                else
                {
                    unresolved += node.OwnAllocatedBytes;
                    unresolvedCount++;
                }
            }

            foreach (ScanNode child in Descending(node.Children))
            {
                if (child.TotalAllocatedBytes < minimumBytes ||
                    !CanAdd(nodes.Count, payload, child.Name, opts))
                {
                    // Not "dropped" - rolled into the Other bucket below, so the children of
                    // this node still sum to exactly its own size.
                    unresolved += child.TotalAllocatedBytes;
                    unresolvedCount++;
                    truncated |= child.TotalAllocatedBytes >= minimumBytes;
                    continue;
                }

                bool hasChildren = child.Children is { Length: > 0 };
                bool willExpand = hasChildren && pending.Depth + 1 < opts.MaximumDepth;

                int index = nodes.Count;
                nodes.Add(new TreemapNode(
                    pending.SelfIndex, child.Name, child.TotalAllocatedBytes,
                    TreemapNodeKind.Directory,
                    // Expandable describes what this projection did NOT resolve. A node whose
                    // children are all about to be projected is not "expandable" - clicking it
                    // would show the user exactly what is already on screen.
                    Expandable: hasChildren && !willExpand,
                    child.Condition));
                payload += EstimateCost(child.Name);

                if (willExpand)
                {
                    frontier.Enqueue(
                        new PendingNode(child, index, pending.Depth + 1),
                        -child.TotalAllocatedBytes);
                }
            }

            if (unresolved > 0)
            {
                nodes.Add(new TreemapNode(
                    pending.SelfIndex, OtherNodeName, unresolved,
                    // Never expandable: there is no single path behind this rectangle to
                    // navigate to. Its size is the whole of its message.
                    TreemapNodeKind.Other, Expandable: false, NodeCondition.None));
                payload += EstimateCost(OtherNodeName);
                aggregated += unresolvedCount;
            }
        }

        return new TreemapProjection(nodes, total, minimumBytes, aggregated, truncated);
    }

    /// <summary>Name used for the synthetic loose-files node.</summary>
    public const string FilesNodeName = "(files here)";

    /// <summary>Name used for the synthetic aggregate node.</summary>
    public const string OtherNodeName = "(smaller items)";

    /// <summary>
    /// The Other bucket always has room reserved, so a budget that runs out mid-directory
    /// still produces a projection whose children sum correctly.
    /// </summary>
    private static bool CanAdd(int count, int payload, string name, TreemapOptions opts) =>
        count + 1 < opts.MaximumNodes &&
        payload + EstimateCost(name) + EstimateCost(OtherNodeName) <= opts.MaximumPayloadBytes;

    private static int EstimateCost(string name) => PerNodeOverheadBytes + EncodedNameCost(name);

    /// <summary>
    /// Upper bound on what one name costs once JSON-encoded.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>Encoding.UTF8.GetByteCount</c>. System.Text.Json's default encoder
    /// escapes every non-ASCII character as <c>\uXXXX</c> — six bytes per UTF-16 code unit,
    /// against the three that UTF-8 would use for the same CJK character. A directory of
    /// Japanese or emoji names would therefore serialize at roughly twice the estimated size
    /// and quietly break the payload cap the renderer depends on, and it would do so only on
    /// the machines least likely to be tested on.
    /// <see cref="Silt.Core.Scanning.TreemapProjector"/>'s cap is checked against the real
    /// encoder in the tests for exactly this reason.
    /// </remarks>
    private static int EncodedNameCost(string name)
    {
        int cost = 0;
        foreach (char c in name)
        {
            // The separator matters: the view root's name is a full path, and every
            // backslash in it is doubled by the encoder.
            cost += c switch
            {
                '"' or '\\' => 2,
                >= ' ' and < (char)0x7F => 1,
                _ => 6,
            };
        }
        return cost;
    }

    private static ScanNode[] Descending(ScanNode[]? children)
    {
        if (children is not { Length: > 0 })
        {
            return [];
        }

        // Copied before sorting: Children is the scan's own array and other views iterate it.
        ScanNode[] ordered = [.. children];
        Array.Sort(ordered, static (a, b) => b.TotalAllocatedBytes.CompareTo(a.TotalAllocatedBytes));
        return ordered;
    }

    /// <param name="SelfIndex">Where this node already sits in the output list.</param>
    private readonly record struct PendingNode(ScanNode Node, int SelfIndex, int Depth);
}
