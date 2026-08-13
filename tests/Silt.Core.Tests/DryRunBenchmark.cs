using System.Diagnostics;
using Silt.Core.Cleanup;
using Silt.Core.Safety;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

/// <summary>
/// Produces a real dry-run against this machine.
/// </summary>
/// <remarks>
/// Read-only by construction: <see cref="CleanupPlanner"/> has no capability to delete
/// anything, so running this cannot modify the machine. That is the point of building the
/// planner before the executor.
/// </remarks>
public sealed class DryRunBenchmark(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_DryRunAgainstThisMachine()
    {
        var planner = new CleanupPlanner(WindowsProtectedPaths.BuildDenylist());

        var stopwatch = Stopwatch.StartNew();
        CleanupPlan plan = planner.Plan(RuleCatalog.All, DateTimeOffset.UtcNow);
        stopwatch.Stop();

        output.WriteLine($"=== dry run in {stopwatch.Elapsed.TotalSeconds:F2} s ===");
        output.WriteLine($"  would reclaim : {Gib(plan.TotalAllocatedBytes):F2} GiB");
        output.WriteLine($"  items         : {plan.TotalItemCount:N0}");
        output.WriteLine($"  files          : {plan.TotalFileCount:N0}");
        output.WriteLine(string.Empty);

        foreach (RulePlan rule in plan.Rules.OrderByDescending(r => r.TotalAllocatedBytes))
        {
            output.WriteLine(
                $"  {Gib(rule.TotalAllocatedBytes),8:F2} GiB  [{rule.Tier}] {rule.DisplayName}");
            output.WriteLine(
                $"                  {rule.Items.Count:N0} items, {rule.TotalFileCount:N0} files");
            output.WriteLine($"                  regeneration: {rule.Regeneration.Description}");
            if (rule.Regeneration.Command is not null)
            {
                output.WriteLine($"                  command: {rule.Regeneration.Command}");
            }
            if (rule.MissingTargets.Count > 0)
            {
                output.WriteLine($"                  not present: {string.Join(", ", rule.MissingTargets)}");
            }
            if (rule.Exclusions.Count > 0)
            {
                output.WriteLine($"                  excluded {rule.Exclusions.Count:N0} items, e.g.:");
                foreach (PlanExclusion exclusion in rule.Exclusions.Take(3))
                {
                    output.WriteLine($"                    {exclusion.Path}");
                    output.WriteLine($"                      {exclusion.Reason}");
                }
            }

            foreach (PlanItem item in rule.Items.OrderByDescending(i => i.AllocatedBytes).Take(4))
            {
                output.WriteLine($"                  + {Gib(item.AllocatedBytes),7:F2} GiB  {item.Path}");
            }
            output.WriteLine(string.Empty);
        }

        // Every rule must carry a regeneration story - Rule 0 is enforced at construction,
        // so this asserts the catalogue actually went through that constructor.
        Assert.All(plan.Rules, r =>
            Assert.False(string.IsNullOrWhiteSpace(r.Regeneration.Description)));
    }

    private static double Gib(long bytes) => bytes / 1024.0 / 1024 / 1024;
}
