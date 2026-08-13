using Silt.Core.Reconciliation;
using Silt.Core.Scanning;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

public sealed class ReconciliationBenchmark(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_ReconcileSystemVolume()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;

        ScanResult scan = new BfsScanner().Scan(new ScanOptions { RootPath = root });
        VolumeReconciliation r = VolumeReconciler.Reconcile(scan, root);

        output.WriteLine($"=== {r.VolumeRoot} ===");
        output.WriteLine($"  capacity : {Gib(r.CapacityBytes),9:F2} GiB");
        output.WriteLine($"  free     : {Gib(r.FreeBytes),9:F2} GiB");
        output.WriteLine($"  used     : {Gib(r.UsedBytes),9:F2} GiB");
        output.WriteLine(string.Empty);
        output.WriteLine("  WATERFALL");

        foreach (ReconciliationLine line in r.Lines)
        {
            output.WriteLine($"  {Gib(line.Bytes),9:F2} GiB  [{line.Kind}] {line.Label}");
            output.WriteLine($"                   {line.Detail}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine($"  unaccounted fraction: {r.UnaccountedFraction:P1}");

        Assert.True(r.CapacityBytes > 0);
    }

    private static double Gib(long bytes) => bytes / 1024.0 / 1024 / 1024;
}
