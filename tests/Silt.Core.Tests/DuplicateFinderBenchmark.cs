using Silt.Core.Duplicates;
using Silt.Core.Safety;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

/// <summary>
/// Runs a real duplicate search against this machine and prints what it cost.
/// </summary>
/// <remarks>
/// Read-only by construction: <see cref="DuplicateFinder"/> has no capability to modify
/// anything, and the cloud-placeholder skip means it will not hydrate synced files either -
/// so running it cannot change the machine or its network usage. Excluded from CI by the
/// Benchmark trait; the numbers it produced are recorded in <c>docs/PLAN.md</c> §5i.
/// </remarks>
public sealed class DuplicateFinderBenchmark(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_DuplicatesUnderLocalAppData()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var result = new DuplicateFinder().Find(new DuplicateOptions
        {
            RootPath = root,
            Denylist = WindowsProtectedPaths.BuildDenylist(),
        });

        output.WriteLine($"=== {root} ===");
        output.WriteLine($"  duration            : {result.Duration.TotalSeconds:F2} s");
        output.WriteLine($"  files examined      : {result.FilesExamined:N0}");
        output.WriteLine($"  candidates (shared size) : {result.CandidateFiles:N0}");
        output.WriteLine($"  bytes read          : {Mib(result.BytesRead):F1} MiB");
        output.WriteLine($"  groups              : {result.Groups.Count:N0}");
        output.WriteLine($"  reclaimable         : {Gib(result.TotalReclaimableBytes):F2} GiB");
        output.WriteLine($"  hardlinks collapsed : {result.HardLinksCollapsed:N0}");
        output.WriteLine($"  cloud skipped       : {result.CloudPlaceholdersSkipped:N0}");
        output.WriteLine($"  denied skipped      : {result.DeniedFilesSkipped:N0}");
        output.WriteLine($"  access denied dirs  : {result.AccessDeniedCount:N0}");
        output.WriteLine($"  unreadable files    : {result.UnreadableFileCount:N0}");

        // The ratio that justifies the funnel: how little of the tree had to be read.
        output.WriteLine(string.Empty);
        output.WriteLine("  top groups:");
        foreach (DuplicateGroup group in result.Groups.Take(10))
        {
            output.WriteLine($"    {Mib(group.ReclaimableBytes),8:F1} MiB  x{group.Paths.Count}  {group.Paths[0]}");
        }
    }

    private static double Mib(long bytes) => bytes / 1024.0 / 1024.0;

    private static double Gib(long bytes) => bytes / 1024.0 / 1024.0 / 1024.0;
}
