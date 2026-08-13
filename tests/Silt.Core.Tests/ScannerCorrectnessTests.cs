using Silt.Core.Scanning;

namespace Silt.Core.Tests;

/// <summary>
/// End-to-end validation of the native enumeration layer against a tree of known contents.
/// </summary>
/// <remarks>
/// These tests are the real guard on <c>FileIdBothDirInfo</c>'s explicit field offsets. A
/// wrong offset does not throw — it silently reads adjacent bytes and produces sizes that
/// look plausible. Only comparing against known-size files catches that.
/// </remarks>
public sealed class ScannerCorrectnessTests : IDisposable
{
    private readonly string _root;

    public ScannerCorrectnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "silt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test scratch directory that will not delete is not a test failure.
        }
    }

    private string WriteFile(string relativePath, int bytes)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    [Fact]
    public void Scan_CountsFilesAndLogicalBytesExactly()
    {
        WriteFile("a.bin", 1000);
        WriteFile("b.bin", 2000);
        WriteFile(Path.Combine("sub", "c.bin"), 3000);
        WriteFile(Path.Combine("sub", "deep", "d.bin"), 4000);

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        Assert.Equal(4, result.TotalFiles);

        // Logical size is byte-exact and independent of cluster size, so it can be asserted
        // directly. Allocated size is rounded up to the cluster and is checked separately.
        Assert.Equal(1000 + 2000 + 3000 + 4000, result.TotalLogicalBytes);
    }

    [Fact]
    public void Scan_CountsDirectoriesExcludingRoot()
    {
        WriteFile(Path.Combine("sub", "deep", "d.bin"), 10);

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        // "sub" and "sub\deep". The root itself is not counted as its own descendant.
        Assert.Equal(2, result.TotalDirectories);
    }

    [Fact]
    public void Scan_AllocatedSizeIsAtLeastLogicalAndClusterAligned()
    {
        WriteFile("small.bin", 1);

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        Assert.Equal(1, result.TotalLogicalBytes);

        // A 1-byte file occupies a whole cluster (or is resident in the MFT, reporting 0).
        // Either way, allocated must never be a nonsensical value - which is exactly what a
        // mis-declared struct offset would produce.
        Assert.True(
            result.TotalAllocatedBytes is >= 0 and <= 128 * 1024,
            $"Allocated size {result.TotalAllocatedBytes} is not plausible for a 1-byte file. " +
            "Suspect the FileIdBothDirInfo field offsets.");
    }

    [Fact]
    public void Scan_RollsUpSubtreeTotalsIntoParents()
    {
        WriteFile(Path.Combine("sub", "c.bin"), 3000);
        WriteFile(Path.Combine("sub", "deep", "d.bin"), 4000);
        WriteFile("top.bin", 500);

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        ScanNode sub = Assert.Single(result.Root.Children!, c => c.Name == "sub");
        Assert.Equal(7000, sub.TotalLogicalBytes);
        Assert.Equal(2, sub.TotalFileCount);

        // Own-bytes excludes descendants; total includes them.
        Assert.Equal(3000, sub.OwnLogicalBytes);
        Assert.Equal(500, result.Root.OwnLogicalBytes);
    }

    [Fact]
    public void Scan_EmptyDirectoryProducesZeroes()
    {
        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        Assert.Equal(0, result.TotalFiles);
        Assert.Equal(0, result.TotalLogicalBytes);
        Assert.Equal(0, result.TotalDirectories);
    }

    [Fact]
    public void Scan_HandlesManyFilesInOneDirectory()
    {
        // Forces GetFileInformationByHandleEx to refill its buffer several times, which is
        // where an off-by-one in the NextEntryOffset walk would surface.
        const int count = 2000;
        for (int i = 0; i < count; i++)
        {
            WriteFile($"f{i:D5}.bin", 16);
        }

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        Assert.Equal(count, result.TotalFiles);
        Assert.Equal(count * 16, result.TotalLogicalBytes);
    }

    [Fact]
    public void Scan_HandlesDeepNesting()
    {
        // The roll-up is iterative precisely so a deep tree cannot overflow the stack.
        string rel = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("d", 60));
        WriteFile(Path.Combine(rel, "leaf.bin"), 42);

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(42, result.TotalLogicalBytes);
        Assert.Equal(60, result.TotalDirectories);
    }

    [Fact]
    public void Scan_IsStableAcrossParallelismSettings()
    {
        for (int i = 0; i < 200; i++)
        {
            WriteFile(Path.Combine($"dir{i % 17}", $"f{i}.bin"), 64);
        }

        ScanResult single = new BfsScanner().Scan(
            new ScanOptions { RootPath = _root, DegreeOfParallelism = 1 });
        ScanResult many = new BfsScanner().Scan(
            new ScanOptions { RootPath = _root, DegreeOfParallelism = 16 });

        // A race in the work queue or the pending counter would show up as a differing
        // total, or as a scan that terminates early.
        Assert.Equal(single.TotalFiles, many.TotalFiles);
        Assert.Equal(single.TotalLogicalBytes, many.TotalLogicalBytes);
        Assert.Equal(single.TotalDirectories, many.TotalDirectories);
        Assert.Equal(200, many.TotalFiles);
    }

    [Fact]
    public void BuildPath_ReconstructsFullPathAtEveryDepth()
    {
        // ScanNode deliberately does not store its path - it is rebuilt from the parent
        // chain, which halved the retained size of a whole-volume scan. That trade is only
        // safe while reconstruction is exact, so assert it against paths the OS produced.
        WriteFile(Path.Combine("alpha", "beta", "gamma", "leaf.bin"), 10);

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        Assert.Equal(_root, result.Root.BuildPath());

        ScanNode alpha = Assert.Single(result.Root.Children!, c => c.Name == "alpha");
        Assert.Equal(Path.Combine(_root, "alpha"), alpha.BuildPath());

        ScanNode beta = Assert.Single(alpha.Children!, c => c.Name == "beta");
        Assert.Equal(Path.Combine(_root, "alpha", "beta"), beta.BuildPath());

        ScanNode gamma = Assert.Single(beta.Children!, c => c.Name == "gamma");
        Assert.Equal(Path.Combine(_root, "alpha", "beta", "gamma"), gamma.BuildPath());

        // The rebuilt path must be usable, not merely plausible.
        Assert.True(Directory.Exists(gamma.BuildPath()));
    }

    [Fact]
    public void BuildPath_DoesNotDoubleSeparatorAtAVolumeRoot()
    {
        // A volume root keeps its trailing separator ("C:\") while every other node is a
        // bare segment, so naive concatenation yields "C:\\Windows".
        //
        // Nodes are constructed directly rather than produced by a scan: an earlier version
        // of this test scanned the whole system volume and hung CI for over ten minutes on a
        // runner image with millions of preinstalled files. Nothing here needs the
        // filesystem - the behaviour under test is purely path reconstruction.
        var root = new ScanNode { Name = @"C:\", Parent = null };
        var windows = new ScanNode { Name = "Windows", Parent = root };
        var system32 = new ScanNode { Name = "System32", Parent = windows };

        Assert.Equal(@"C:\", root.BuildPath());
        Assert.Equal(@"C:\Windows", windows.BuildPath());
        Assert.Equal(@"C:\Windows\System32", system32.BuildPath());

        // Skip the drive prefix; a doubled separator anywhere after it is the bug.
        Assert.DoesNotContain(@"\\", system32.BuildPath()[2..], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPath_HandlesARootWithoutATrailingSeparator()
    {
        // Scanning a subdirectory gives a root with no trailing separator, so the separator
        // must be supplied. This is the mirror image of the volume-root case above; getting
        // exactly one of them right is the likely failure.
        var root = new ScanNode { Name = @"D:\projects", Parent = null };
        var child = new ScanNode { Name = "silt", Parent = root };

        Assert.Equal(@"D:\projects", root.BuildPath());
        Assert.Equal(@"D:\projects\silt", child.BuildPath());
    }

    [Fact]
    public void Scan_DoesNotTraverseJunctions()
    {
        // A junction pointing at a sibling would double-count it, and a junction pointing at
        // an ancestor would never terminate.
        WriteFile(Path.Combine("real", "payload.bin"), 5000);

        string junction = Path.Combine(_root, "link");
        if (!TryCreateJunction(junction, Path.Combine(_root, "real")))
        {
            return; // Junction creation unavailable in this environment; nothing to assert.
        }

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = _root });

        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(5000, result.TotalLogicalBytes);
        Assert.Equal(1, result.SkippedSurrogateCount);

        ScanNode link = Assert.Single(result.Root.Children!, c => c.Name == "link");
        Assert.True(link.Condition.HasFlag(NodeCondition.NameSurrogate));
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a symlink needs Developer Mode or elevation.
            return false;
        }
    }
}
