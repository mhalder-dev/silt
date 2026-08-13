using System.Globalization;
using System.Text.Json;
using Silt.Core.Scanning;

namespace Silt.Core.Tests;

/// <summary>
/// Invariants for the treemap projection.
/// </summary>
/// <remarks>
/// The load-bearing one is area conservation: every expanded node's children must sum to
/// exactly that node's size. A treemap exists to convey proportion, so a projection that
/// silently loses bytes does not degrade gracefully - it draws a picture that is wrong in the
/// only dimension it communicates, while looking entirely plausible.
/// </remarks>
public sealed class TreemapProjectorTests
{
    /// <summary>The API's own serializer settings, so this measures the real wire format.</summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds a directory node and wires the parent link both ways.</summary>
    private static ScanNode Dir(string name, long ownBytes, params ScanNode[] children)
    {
        var node = new ScanNode { Name = name, OwnAllocatedBytes = ownBytes };

        if (children.Length > 0)
        {
            // Parent is init-only, so children are rebuilt against the finished parent.
            ScanNode[] linked = new ScanNode[children.Length];
            for (int i = 0; i < children.Length; i++)
            {
                linked[i] = Reparent(children[i], node);
            }
            node.Children = linked;
        }

        node.TotalAllocatedBytes = ownBytes + (node.Children ?? []).Sum(c => c.TotalAllocatedBytes);
        return node;
    }

    private static ScanNode Reparent(ScanNode node, ScanNode parent)
    {
        var copy = new ScanNode
        {
            Name = node.Name,
            Parent = parent,
            OwnAllocatedBytes = node.OwnAllocatedBytes,
            Condition = node.Condition,
        };

        if (node.Children is { Length: > 0 })
        {
            ScanNode[] linked = new ScanNode[node.Children.Length];
            for (int i = 0; i < node.Children.Length; i++)
            {
                linked[i] = Reparent(node.Children[i], copy);
            }
            copy.Children = linked;
        }

        copy.TotalAllocatedBytes =
            copy.OwnAllocatedBytes + (copy.Children ?? []).Sum(c => c.TotalAllocatedBytes);
        return copy;
    }

    /// <summary>Sums each node's projected children and compares with the node itself.</summary>
    private static void AssertAreaIsConserved(TreemapProjection projection)
    {
        long[] childSum = new long[projection.Nodes.Count];
        bool[] hasChildren = new bool[projection.Nodes.Count];

        for (int i = 1; i < projection.Nodes.Count; i++)
        {
            TreemapNode node = projection.Nodes[i];

            // A parent that appeared after its child would mean the renderer could not lay
            // the projection out in one forward pass.
            Assert.InRange(node.ParentIndex, 0, i - 1);

            childSum[node.ParentIndex] += node.Bytes;
            hasChildren[node.ParentIndex] = true;
        }

        for (int i = 0; i < projection.Nodes.Count; i++)
        {
            if (hasChildren[i])
            {
                Assert.Equal(projection.Nodes[i].Bytes, childSum[i]);
            }
        }
    }

    [Fact]
    public void Project_GivesLooseFilesTheirOwnRectangle()
    {
        // Without a node for the parent's own files, 'sub' would be laid out as if it were
        // the whole of 'root' and would be drawn four times too large.
        ScanNode root = Dir("C:\\root", ownBytes: 300, Dir("sub", 100));

        TreemapProjection projection = TreemapProjector.Project(root);

        Assert.Equal(400, projection.TotalBytes);
        Assert.Contains(projection.Nodes, n => n.Kind == TreemapNodeKind.Files && n.Bytes == 300);
        AssertAreaIsConserved(projection);
    }

    [Fact]
    public void Project_RollsCulledChildrenIntoAnAggregateRatherThanDroppingThem()
    {
        ScanNode root = Dir(
            "C:\\root",
            ownBytes: 0,
            Dir("big", 1_000_000_000),
            Dir("dust-a", 5),
            Dir("dust-b", 7));

        TreemapProjection projection = TreemapProjector.Project(root);

        Assert.DoesNotContain(projection.Nodes, n => n.Name == "dust-a");
        TreemapNode other = projection.Nodes.Single(n => n.Kind == TreemapNodeKind.Other);
        Assert.Equal(12, other.Bytes);
        Assert.Equal(2, projection.AggregatedNodeCount);
        AssertAreaIsConserved(projection);
    }

    [Fact]
    public void Project_MarksADirectoryExpandableOnlyWhenItsChildrenAreNotShown()
    {
        ScanNode root = Dir("C:\\root", 0, Dir("a", 0, Dir("a1", 1000)));

        TreemapProjection shallow = TreemapProjector.Project(
            root, new TreemapOptions { MaximumDepth = 1 });

        Assert.True(shallow.Nodes.Single(n => n.Name == "a").Expandable);
        Assert.DoesNotContain(shallow.Nodes, n => n.Name == "a1");

        // With the children already drawn, offering to zoom in would show the user exactly
        // what is already on screen.
        TreemapProjection deeper = TreemapProjector.Project(root);
        Assert.False(deeper.Nodes.Single(n => n.Name == "a").Expandable);
        Assert.Contains(deeper.Nodes, n => n.Name == "a1");
    }

    [Fact]
    public void Project_ResolvesTheLargestSubtreesFirstWhenTheBudgetIsTight()
    {
        // Budget for only a handful of nodes. The one worth spending it on is the big one; a
        // breadth-first projection would spend it on whichever child happened to sort first.
        ScanNode root = Dir(
            "C:\\root",
            0,
            Dir("small", 0, Dir("small-child", 1_000_000)),
            Dir("huge", 0, Dir("huge-child", 900_000_000)));

        TreemapProjection projection = TreemapProjector.Project(
            root, new TreemapOptions { MaximumNodes = 5 });

        Assert.Contains(projection.Nodes, n => n.Name == "huge-child");
        AssertAreaIsConserved(projection);
    }

    /// <summary>
    /// Serializes exactly as the API does, so this measures the wire format rather than the
    /// object graph. The encoder is the thing under test, so it is not configured away.
    /// </summary>
    private static int MeasureWireBytes(TreemapProjection projection)
    {
        var payload = projection.Nodes
            .Select(n => new
            {
                p = n.ParentIndex,
                n = n.Name,
                b = n.Bytes,
                k = n.Kind.ToString(),
                x = n.Expandable,
            })
            .ToArray();

        return JsonSerializer.SerializeToUtf8Bytes(payload, WireOptions).Length;
    }

    private static ScanNode WideTree(char nameChar, int nameLength)
    {
        var children = new ScanNode[40_000];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = Dir(
                new string(nameChar, nameLength) + i.ToString("D5", CultureInfo.InvariantCulture),
                1_000_000 + i);
        }

        return Dir("C:\\root", 0, children);
    }

    [Fact]
    public void Project_StaysWithinTheEightMegabytePayloadCapWithAsciiNames()
    {
        TreemapProjection projection = TreemapProjector.Project(WideTree('n', 250));

        Assert.InRange(MeasureWireBytes(projection), 1, 8 * 1024 * 1024);
        Assert.True(projection.Truncated, "the fixture deliberately exceeds the budget");
        AssertAreaIsConserved(projection);
    }

    [Fact]
    public void Project_StaysWithinThePayloadCapWhenNamesAreNotAscii()
    {
        // The case a naive estimate gets wrong. System.Text.Json escapes each of these to
        // \uXXXX - six bytes, not the three that UTF-8 would need - so sizing the budget with
        // Encoding.UTF8.GetByteCount would overshoot the cap by roughly 2x, and would do so
        // only on machines whose directory names are not English.
        TreemapProjection projection = TreemapProjector.Project(WideTree('\u65e5', 250));

        Assert.InRange(MeasureWireBytes(projection), 1, 8 * 1024 * 1024);
        Assert.True(projection.Truncated, "the fixture deliberately exceeds the budget");
        AssertAreaIsConserved(projection);
    }

    [Fact]
    public void Project_KeepsAreaConservedAcrossADeepUnevenTree()
    {
        // Every awkward feature at once: loose files at several levels, a deep chain, wide
        // fan-out, and sizes spanning nine orders of magnitude.
        var rng = new Random(20260814);
        ScanNode root = Build(depth: 0);

        TreemapProjection projection = TreemapProjector.Project(root);

        AssertAreaIsConserved(projection);
        Assert.Equal(root.TotalAllocatedBytes, projection.Nodes[0].Bytes);

        ScanNode Build(int depth)
        {
            if (depth >= 6)
            {
                return Dir($"leaf{rng.Next(1000)}", rng.NextInt64(1, 1_000_000_000));
            }

            int fanOut = rng.Next(1, 6);
            var kids = new ScanNode[fanOut];
            for (int i = 0; i < fanOut; i++)
            {
                kids[i] = Build(depth + 1);
            }

            return Dir($"d{depth}-{rng.Next(1000)}", rng.NextInt64(0, 100_000_000), kids);
        }
    }
}
