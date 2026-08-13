using Silt.Core.Scanning;
using Silt.Safety;

namespace Silt.Core.Cleanup;

/// <summary>One thing a plan proposes to remove.</summary>
public sealed record PlanItem(
    string Path,
    long AllocatedBytes,
    bool IsDirectory,
    DateTimeOffset LastWriteUtc,
    long FileCount);

/// <summary>Something a rule matched but the plan refuses to include, and why.</summary>
public sealed record PlanExclusion(string Path, string Reason, string Rule);

/// <summary>What one rule proposes.</summary>
public sealed record RulePlan(
    string RuleId,
    string DisplayName,
    string Description,
    SafetyTier Tier,
    Regeneration Regeneration,
    IReadOnlyList<PlanItem> Items,
    long TotalAllocatedBytes,
    long TotalFileCount,
    IReadOnlyList<PlanExclusion> Exclusions,
    IReadOnlyList<string> MissingTargets);

/// <summary>A complete dry-run.</summary>
/// <remarks>
/// A plan is a description, never an instruction. It carries no capability to act, and
/// producing one cannot delete anything.
/// </remarks>
public sealed record CleanupPlan(
    DateTimeOffset CreatedAt,
    IReadOnlyList<RulePlan> Rules,
    long TotalAllocatedBytes,
    long TotalFileCount,
    int TotalItemCount);

/// <summary>
/// Turns rules into an exact, reviewable list of what would be removed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dry-run is the only planning path.</b> There is no "just delete it" entry point that
/// bypasses this, so whatever is executed later is something a plan has already enumerated
/// and the user has seen.
/// </para>
/// <para>
/// Every candidate is checked against the denylist individually — not the rule's target
/// directory, every item. A rule cannot widen its own reach by pointing somewhere unexpected,
/// because the check happens at the item, after expansion.
/// </para>
/// </remarks>
public sealed class CleanupPlanner(Denylist denylist, IVolumeScanner? scanner = null)
{
    private readonly Denylist _denylist = denylist
        ?? throw new ArgumentNullException(nameof(denylist));

    private readonly IVolumeScanner _scanner = scanner ?? new BfsScanner();

    public CleanupPlan Plan(
        IEnumerable<CleanupRule> rules,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var rulePlans = new List<RulePlan>();

        foreach (CleanupRule rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rulePlans.Add(PlanRule(rule, now, cancellationToken));
        }

        return new CleanupPlan(
            now,
            rulePlans,
            rulePlans.Sum(r => r.TotalAllocatedBytes),
            rulePlans.Sum(r => r.TotalFileCount),
            rulePlans.Sum(r => r.Items.Count));
    }

    private RulePlan PlanRule(CleanupRule rule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var items = new List<PlanItem>();
        var exclusions = new List<PlanExclusion>();
        var missing = new List<string>();

        foreach (RuleTarget target in rule.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<string> directories = TargetExpansion.Expand(target.PathTemplate);
            if (directories.Count == 0)
            {
                missing.Add(target.PathTemplate);
                continue;
            }

            foreach (string directory in directories)
            {
                CollectFrom(rule, target, directory, now, items, exclusions, cancellationToken);
            }
        }

        return new RulePlan(
            rule.Id,
            rule.DisplayName,
            rule.Description,
            rule.Tier,
            rule.Regeneration,
            items,
            items.Sum(i => i.AllocatedBytes),
            items.Sum(i => i.FileCount),
            exclusions,
            missing);
    }

    private void CollectFrom(
        CleanupRule rule,
        RuleTarget target,
        string directory,
        DateTimeOffset now,
        List<PlanItem> items,
        List<PlanExclusion> exclusions,
        CancellationToken cancellationToken)
    {
        // One scan of the target gives every subdirectory's size at once, rather than
        // sizing each candidate separately.
        ScanResult scan;
        try
        {
            scan = _scanner.Scan(
                new ScanOptions { RootPath = directory, DeduplicateHardLinks = false },
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exclusions.Add(new PlanExclusion(directory, $"Could not be read: {ex.Message}", rule.Id));
            return;
        }

        foreach (ScanNode child in scan.Root.Children ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (target.Kind == RuleTargetKind.MatchingFiles)
            {
                continue; // directories are not candidates for a file-glob target
            }

            string path = Path.Combine(directory, child.Name);
            Consider(rule, path, child.TotalAllocatedBytes, true, child.TotalFileCount,
                now, items, exclusions);
        }

        // Files sitting directly in the target directory.
        IEnumerable<string> files;
        try
        {
            files = target.Kind == RuleTargetKind.MatchingFiles
                ? Directory.EnumerateFiles(directory, target.Glob ?? "*", SearchOption.TopDirectoryOnly)
                : Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exclusions.Add(new PlanExclusion(directory, $"Could not be listed: {ex.Message}", rule.Id));
            return;
        }

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                exclusions.Add(new PlanExclusion(file, $"Could not be measured: {ex.Message}", rule.Id));
                continue;
            }

            Consider(rule, file, size, false, 1, now, items, exclusions);
        }
    }

    private void Consider(
        CleanupRule rule,
        string path,
        long bytes,
        bool isDirectory,
        long fileCount,
        DateTimeOffset now,
        List<PlanItem> items,
        List<PlanExclusion> exclusions)
    {
        // The denylist is consulted for every individual item, never for the rule's target.
        DenyVerdict verdict = _denylist.Check(path);
        if (verdict.IsDenied)
        {
            exclusions.Add(new PlanExclusion(path, verdict.Reason ?? "Protected.", rule.Id));
            return;
        }

        DateTimeOffset lastWrite;
        try
        {
            lastWrite = isDirectory
                ? new DirectoryInfo(path).LastWriteTimeUtc
                : new FileInfo(path).LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exclusions.Add(new PlanExclusion(path, $"Age could not be determined: {ex.Message}", rule.Id));
            return;
        }

        // Age is tested per item.
        //
        // Testing the rule's directory instead would permanently disqualify
        // %LOCALAPPDATA%\Temp, which something writes to every few seconds — silently
        // disabling the highest-value rule in the product. Review caught exactly that in the
        // original veto design.
        if (rule.MinimumAge is { } minimumAge && now - lastWrite < minimumAge)
        {
            exclusions.Add(new PlanExclusion(
                path,
                $"Modified {FormatAge(now - lastWrite)} ago; the rule requires " +
                $"{FormatAge(minimumAge)} of inactivity.",
                rule.Id));
            return;
        }

        items.Add(new PlanItem(path, bytes, isDirectory, lastWrite, fileCount));
    }

    private static string FormatAge(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h",
        { TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes}m",
        _ => "moments",
    };
}
