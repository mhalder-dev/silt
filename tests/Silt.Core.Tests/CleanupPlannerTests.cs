using Silt.Core.Cleanup;
using Silt.Safety;

namespace Silt.Core.Tests;

public sealed class CleanupPlannerTests : IDisposable
{
    private readonly string _root;

    public CleanupPlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "silt-plan", Guid.NewGuid().ToString("N"));
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
        }
    }

    private string WriteFile(string relative, int bytes, DateTimeOffset? lastWrite = null)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        if (lastWrite is { } stamp)
        {
            File.SetLastWriteTimeUtc(full, stamp.UtcDateTime);
        }
        return full;
    }

    private static CleanupRule RuleFor(string path, TimeSpan? minimumAge = null) => new(
        id: "test.rule",
        displayName: "Test rule",
        description: "For tests.",
        tier: SafetyTier.AlwaysSafe,
        targets: [new RuleTarget(path, RuleTargetKind.DirectoryContents)],
        regeneration: new Regeneration("Regenerated on demand."),
        minimumAge: minimumAge);

    private static CleanupPlanner PlannerWith(params ProtectedPath[] protectedPaths) =>
        new(new Denylist(protectedPaths));

    // --- Rule 0 ------------------------------------------------------------

    [Fact]
    public void Rule0_ARuleWithoutARegenerationStoryCannotBeConstructed()
    {
        // Rule 0 is enforced by the type system rather than by review: a rule that cannot
        // say how its data comes back cannot be created, so it can never be executed.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => new CleanupRule(
            id: "bad.rule",
            displayName: "Bad rule",
            description: "No regeneration story.",
            tier: SafetyTier.AlwaysSafe,
            targets: [new RuleTarget(@"C:\whatever", RuleTargetKind.DirectoryContents)],
            regeneration: new Regeneration("   ")));

        Assert.Contains("Rule 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule0_EveryShippedRuleCarriesARegenerationStory()
    {
        Assert.All(RuleCatalog.All, rule =>
            Assert.False(string.IsNullOrWhiteSpace(rule.Regeneration.Description)));
    }

    [Fact]
    public void ShippedRules_HaveUniqueIds()
    {
        string[] ids = [.. RuleCatalog.All.Select(r => r.Id)];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // --- Denylist enforcement ---------------------------------------------

    [Fact]
    public void Plan_RefusesItemsTheDenylistProtectsEvenWhenARuleTargetsThem()
    {
        // A rule pointing at protected data must not be able to reach it. The check happens
        // per item after expansion, so a rule cannot widen its own reach.
        WriteFile(Path.Combine("keep", "precious.txt"), 1000);
        WriteFile(Path.Combine("drop", "junk.txt"), 1000);

        CleanupPlanner planner = PlannerWith(
            new ProtectedPath(Path.Combine(_root, "keep"), "Precious."));

        CleanupPlan plan = planner.Plan([RuleFor(_root)], DateTimeOffset.UtcNow);
        RulePlan rule = Assert.Single(plan.Rules);

        Assert.DoesNotContain(rule.Items, i => i.Path.Contains("keep", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rule.Items, i => i.Path.Contains("drop", StringComparison.OrdinalIgnoreCase));

        PlanExclusion exclusion = Assert.Single(
            rule.Exclusions, e => e.Path.Contains("keep", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Precious.", exclusion.Reason);
    }

    [Fact]
    public void Plan_RefusesSecretsByNameEvenInsideAnAllowedDirectory()
    {
        WriteFile("appsettings.json", 500);
        WriteFile("ordinary.log", 500);

        CleanupPlan plan = PlannerWith().Plan([RuleFor(_root)], DateTimeOffset.UtcNow);
        RulePlan rule = Assert.Single(plan.Rules);

        Assert.DoesNotContain(rule.Items, i => i.Path.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rule.Items, i => i.Path.EndsWith("ordinary.log", StringComparison.OrdinalIgnoreCase));
    }

    // --- Age, applied per item --------------------------------------------

    [Fact]
    public void Plan_AppliesTheAgeTestPerItemNotToTheTargetDirectory()
    {
        // The scenario that would silently disable the highest-value rule: a directory that
        // is constantly written to, holding items that are individually stale.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WriteFile("stale.tmp", 2000, now.AddDays(-30));
        WriteFile("fresh.tmp", 2000, now.AddMinutes(-1));

        // Touch the directory itself so it looks freshly modified.
        Directory.SetLastWriteTimeUtc(_root, now.UtcDateTime);

        CleanupPlan plan = PlannerWith().Plan(
            [RuleFor(_root, TimeSpan.FromDays(7))], now);
        RulePlan rule = Assert.Single(plan.Rules);

        PlanItem item = Assert.Single(rule.Items);
        Assert.EndsWith("stale.tmp", item.Path, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(rule.Exclusions, e =>
            e.Path.EndsWith("fresh.tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ExplainsWhyAnItemWasTooRecent()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WriteFile("fresh.tmp", 100, now.AddDays(-2));

        CleanupPlan plan = PlannerWith().Plan([RuleFor(_root, TimeSpan.FromDays(7))], now);

        PlanExclusion exclusion = Assert.Single(Assert.Single(plan.Rules).Exclusions);
        Assert.Contains("2d ago", exclusion.Reason, StringComparison.Ordinal);
        Assert.Contains("7d", exclusion.Reason, StringComparison.Ordinal);
    }

    // --- Shape of a plan ---------------------------------------------------

    [Fact]
    public void Plan_DoesNotModifyTheFilesystem()
    {
        // Planning is a description, never an instruction. The planner has no deletion
        // capability at all, and this asserts that end to end.
        WriteFile("a.tmp", 1000);
        WriteFile(Path.Combine("sub", "b.tmp"), 1000);

        string[] before = [.. Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)];

        PlannerWith().Plan([RuleFor(_root)], DateTimeOffset.UtcNow);

        string[] after = [.. Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)];
        Assert.Equal(before, after);
    }

    [Fact]
    public void Plan_ReportsAbsentTargetsRatherThanFailing()
    {
        // A rule for software that is not installed is normal, not an error.
        var rule = RuleFor(Path.Combine(_root, "does-not-exist"));

        CleanupPlan plan = PlannerWith().Plan([rule], DateTimeOffset.UtcNow);
        RulePlan planned = Assert.Single(plan.Rules);

        Assert.Empty(planned.Items);
        Assert.Single(planned.MissingTargets);
    }

    [Fact]
    public void Plan_TotalsMatchTheSumOfItems()
    {
        WriteFile("a.tmp", 4096);
        WriteFile("b.tmp", 8192);

        CleanupPlan plan = PlannerWith().Plan([RuleFor(_root)], DateTimeOffset.UtcNow);

        Assert.Equal(plan.Rules.Sum(r => r.TotalAllocatedBytes), plan.TotalAllocatedBytes);
        Assert.Equal(2, plan.TotalItemCount);
    }

    [Fact]
    public void Plan_MatchingFilesTargetOnlyTakesFilesMatchingTheGlob()
    {
        WriteFile("thumbcache_256.db", 1000);
        WriteFile("thumbcache_1024.db", 1000);
        WriteFile("important.db", 1000);

        var rule = new CleanupRule(
            id: "glob.rule",
            displayName: "Glob rule",
            description: "For tests.",
            tier: SafetyTier.AlwaysSafe,
            targets: [new RuleTarget(_root, RuleTargetKind.MatchingFiles, Glob: "thumbcache_*.db")],
            regeneration: new Regeneration("Regenerated."));

        CleanupPlan plan = PlannerWith().Plan([rule], DateTimeOffset.UtcNow);
        RulePlan planned = Assert.Single(plan.Rules);

        Assert.Equal(2, planned.Items.Count);
        Assert.DoesNotContain(planned.Items, i =>
            i.Path.EndsWith("important.db", StringComparison.OrdinalIgnoreCase));
    }

    // --- Target expansion --------------------------------------------------

    [Fact]
    public void Expand_ResolvesAWildcardSegmentToEveryMatchingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Profile 1", "Cache"));
        Directory.CreateDirectory(Path.Combine(_root, "Profile 2", "Cache"));
        Directory.CreateDirectory(Path.Combine(_root, "Profile 3"));  // no Cache child

        IReadOnlyList<string> expanded = TargetExpansion.Expand(Path.Combine(_root, "*", "Cache"));

        Assert.Equal(2, expanded.Count);
        Assert.All(expanded, p => Assert.EndsWith("Cache", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Expand_ReturnsNothingForAnUnresolvedEnvironmentVariable()
    {
        // A literal "%NOPE%" is not a path; the target is simply absent on this machine.
        Assert.Empty(TargetExpansion.Expand(@"%SILT_NO_SUCH_VARIABLE%\cache"));
    }

    [Fact]
    public void Expand_ReturnsNothingWhenTheDirectoryIsAbsent()
    {
        Assert.Empty(TargetExpansion.Expand(Path.Combine(_root, "absent")));
    }
}
