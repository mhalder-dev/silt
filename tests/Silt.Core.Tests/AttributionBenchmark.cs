using Silt.Core.Attribution;
using Silt.Core.Scanning;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

public sealed class AttributionBenchmark(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_AttributeThisMachine()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;

        ScanResult scan = new BfsScanner().Scan(new ScanOptions { RootPath = root });

        // 100 MiB floor: below that the list becomes hundreds of rows of noise and stops
        // being a tool for finding what is actually large.
        IReadOnlyList<AppFootprint> apps =
            new AppAttributor().Attribute(scan, minimumBytes: 100L * 1024 * 1024);

        output.WriteLine($"=== per-application footprints on {root} ===");
        output.WriteLine($"    ({apps.Count} applications over 100 MiB)");
        output.WriteLine(string.Empty);

        foreach (AppFootprint app in apps.Take(25))
        {
            string split = app.IsSplitAcrossLocations
                ? $"  << SPLIT ACROSS {app.Locations.Count} LOCATIONS"
                : string.Empty;

            output.WriteLine(
                $"  {Gib(app.TotalAllocatedBytes),7:F2} GiB  {app.DisplayName}{split}");

            if (app.Publisher is not null)
            {
                output.WriteLine($"                 publisher: {app.Publisher}");
            }

            foreach (AppLocation location in app.Locations)
            {
                output.WriteLine(
                    $"                 {Gib(location.AllocatedBytes),7:F2} GiB " +
                    $"[{location.Kind}] {location.Path}");
            }
            output.WriteLine(string.Empty);
        }

        Assert.NotEmpty(apps);
    }

    private static double Gib(long bytes) => bytes / 1024.0 / 1024 / 1024;
}
