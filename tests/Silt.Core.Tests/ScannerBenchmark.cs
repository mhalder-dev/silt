using System.Diagnostics;
using Silt.Core.Scanning;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

/// <summary>
/// Measures the scanner against the real machine.
/// </summary>
/// <remarks>
/// Traited <c>Benchmark</c> and excluded from CI: results depend on the host's disk, cache
/// state, and antivirus, so a threshold assertion here would be a flake generator. Run it
/// deliberately:
/// <code>dotnet test --filter "Category=Benchmark" -l "console;verbosity=detailed"</code>
/// </remarks>
public sealed class ScannerBenchmark(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_UserProfile()
    {
        Run(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_WholeSystemVolume()
    {
        Run(Path.GetPathRoot(Environment.SystemDirectory)!);
    }

    private void Run(string root)
    {
        if (!Directory.Exists(root))
        {
            output.WriteLine($"SKIP - {root} not present");
            return;
        }

        var scanner = new BfsScanner();

        // Warm pass populates the OS metadata cache so the reported figure reflects steady
        // state rather than a cold-boot outlier. Both numbers are printed.
        var coldSw = Stopwatch.StartNew();
        ScanResult cold = scanner.Scan(new ScanOptions { RootPath = root });
        coldSw.Stop();

        var warmSw = Stopwatch.StartNew();
        ScanResult warm = scanner.Scan(new ScanOptions { RootPath = root });
        warmSw.Stop();

        output.WriteLine($"=== {root} ===");
        output.WriteLine($"  first pass    : {cold.Duration.TotalSeconds,8:F2} s");
        output.WriteLine($"  second pass   : {warm.Duration.TotalSeconds,8:F2} s");
        output.WriteLine($"  files         : {warm.TotalFiles,12:N0}");
        output.WriteLine($"  directories   : {warm.TotalDirectories,12:N0}");
        output.WriteLine($"  allocated     : {warm.TotalAllocatedBytes / 1024.0 / 1024 / 1024,8:F2} GiB");
        output.WriteLine($"  logical       : {warm.TotalLogicalBytes / 1024.0 / 1024 / 1024,8:F2} GiB");
        output.WriteLine($"  access denied : {warm.AccessDeniedCount,12:N0}");
        output.WriteLine($"  failed        : {warm.FailedCount,12:N0}");
        output.WriteLine($"  junctions skipped: {warm.SkippedSurrogateCount,9:N0}");
        output.WriteLine($"  hardlink dedup: {warm.HardLinkFilesDeduplicated,12:N0} files, " +
                         $"{warm.HardLinkBytesDeduplicated / 1024.0 / 1024 / 1024:F2} GiB not double-counted");

        double filesPerSecond = warm.TotalFiles / Math.Max(0.001, warm.Duration.TotalSeconds);
        output.WriteLine($"  throughput    : {filesPerSecond,12:N0} files/s");

        output.WriteLine(string.Empty);
        output.WriteLine("  Top 15 by allocated size:");
        foreach (ScanNode child in (warm.Root.Children ?? [])
                     .OrderByDescending(c => c.TotalAllocatedBytes)
                     .Take(15))
        {
            output.WriteLine(
                $"    {child.TotalAllocatedBytes / 1024.0 / 1024 / 1024,8:F2} GiB  {child.Name}");
        }

        Assert.True(warm.TotalFiles > 0, "Scan returned no files at all.");
    }
}
