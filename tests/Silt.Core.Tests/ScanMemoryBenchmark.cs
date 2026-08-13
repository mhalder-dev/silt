using Silt.Core.Scanning;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

/// <summary>
/// Measures the memory actually <em>retained</em> by a completed scan tree.
/// </summary>
/// <remarks>
/// Working-set numbers from Task Manager or Win32 include uncollected garbage and native
/// allocator slack, so they overstate what a result costs to hold. This forces a full
/// collection and measures the managed heap delta with the tree still rooted, which is the
/// figure that decides whether the index design is affordable.
/// </remarks>
public sealed class ScanMemoryBenchmark(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_RetainedBytesPerDirectory()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;

        long before = GetSettledManagedBytes();

        ScanResult result = new BfsScanner().Scan(new ScanOptions { RootPath = root });

        long after = GetSettledManagedBytes();

        // GC.KeepAlive is essential: without it the JIT may consider `result` dead by the
        // time the second measurement runs, collect the entire tree, and report a delta of
        // roughly zero - which would look like a spectacular result and mean nothing.
        GC.KeepAlive(result);

        long retained = after - before;
        double perDirectory = (double)retained / Math.Max(1, result.TotalDirectories);

        output.WriteLine($"=== retained scan tree for {root} ===");
        output.WriteLine($"  directories   : {result.TotalDirectories,12:N0}");
        output.WriteLine($"  files         : {result.TotalFiles,12:N0}");
        output.WriteLine($"  retained heap : {retained / 1024.0 / 1024,12:F1} MiB");
        output.WriteLine($"  per directory : {perDirectory,12:F0} bytes");

        Assert.True(retained > 0, "Measured no retained memory; the tree was collected early.");
    }

    private static long GetSettledManagedBytes()
    {
        // Two passes: the first collects, the second reclaims anything the finalizer queue
        // released during the first.
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
