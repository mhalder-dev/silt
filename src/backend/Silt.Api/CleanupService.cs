using System.Collections.Concurrent;
using Silt.Core.Cleanup;
using Silt.Core.Safety;
using Silt.Safety;

namespace Silt.Api;

/// <summary>
/// Owns cleanup planning and execution for the process.
/// </summary>
/// <remarks>
/// <para>
/// Execution accepts only a <c>planId</c> issued by a previous dry run. There is no endpoint
/// that takes paths and deletes them — the only way to remove anything is to reference a
/// plan the user has already been shown, which makes "dry-run is the only planning path" a
/// property of the API surface rather than a convention.
/// </para>
/// <para>
/// Plans are single-use. Executing one consumes it, so a stale plan cannot be replayed
/// against a filesystem that has moved on.
/// </para>
/// </remarks>
public sealed class CleanupService
{
    private readonly ConcurrentDictionary<string, CleanupPlan> _plans = new(StringComparer.Ordinal);
    private readonly Denylist _denylist;
    private readonly CleanupPlanner _planner;
    private readonly CleanupExecutor _executor;
    private readonly OperationJournal _journal;

    public CleanupService(
        Denylist? denylist = null,
        OperationJournal? journal = null,
        IRecycleBinProbe? recycleBin = null)
    {
        _denylist = denylist ?? WindowsProtectedPaths.BuildDenylist();
        _journal = journal ?? new OperationJournal();
        _planner = new CleanupPlanner(_denylist);
        _executor = new CleanupExecutor(_denylist, _journal, null, recycleBin);
    }

    /// <summary>Verifies the denylist before anything is offered. Empty means healthy.</summary>
    public IReadOnlyList<CanaryFailure> VerifySafety() => StartupCanary.Verify(_denylist);

    public CleanupPlanDto CreatePlan(DateTimeOffset now)
    {
        CleanupPlan plan = _planner.Plan(RuleCatalog.All, now);
        string planId = Guid.NewGuid().ToString("N")[..12];
        _plans[planId] = plan;

        return Map(planId, plan);
    }

    public CleanupPlanDto? GetPlan(string planId) =>
        _plans.TryGetValue(planId, out CleanupPlan? plan) ? Map(planId, plan) : null;

    /// <summary>
    /// Executes one rule from a previously issued plan.
    /// </summary>
    /// <remarks>
    /// The plan is removed on execution. Re-running a plan against a filesystem that has
    /// changed since it was reviewed would delete things the user never saw.
    /// </remarks>
    public ExecutionResultDto? Execute(string planId, string ruleId, DateTimeOffset now)
    {
        if (!_plans.TryGetValue(planId, out CleanupPlan? plan))
        {
            return null;
        }

        RulePlan? rulePlan = plan.Rules.FirstOrDefault(r =>
            string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

        if (rulePlan is null)
        {
            return null;
        }

        string operationId = Guid.NewGuid().ToString("N")[..12];
        ExecutionResult result = _executor.Execute(rulePlan, operationId, now);

        // A refused batch leaves the plan usable, so the user can split it and retry.
        if (result.Executed)
        {
            CleanupPlan remaining = plan with
            {
                Rules = [.. plan.Rules.Where(r => !string.Equals(
                    r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))],
            };
            _plans[planId] = remaining;
        }

        return new ExecutionResultDto(
            result.OperationId,
            result.RuleId,
            result.Executed,
            result.Refusal.ToString(),
            result.RefusalMessage,
            result.ItemsDeleted,
            result.ItemsFailed,
            result.BytesDeleted,
            result.RecycleBinAvailableBytes,
            [.. result.Outcomes
                .Where(o => !o.Succeeded)
                .Take(25)
                .Select(o => new FailedItemDto(o.Path, o.Failure ?? "Unknown."))]);
    }

    public JournalDto GetJournal(int limit)
    {
        IReadOnlyList<JournalEntry> entries = _journal.Read();
        JournalVerification verification = _journal.Verify();

        return new JournalDto(
            verification.Intact,
            verification.FirstBreakAt,
            entries.Count,
            [.. entries
                .OrderByDescending(e => e.At)
                .Take(limit)
                .Select(e => new JournalEntryDto(
                    e.OperationId, e.At, e.RuleId, e.Path, e.Bytes,
                    e.Succeeded, e.Recoverable, e.Failure))]);
    }

    private static CleanupPlanDto Map(string planId, CleanupPlan plan) => new(
        planId,
        plan.CreatedAt,
        plan.TotalAllocatedBytes,
        plan.TotalFileCount,
        plan.TotalItemCount,
        [.. plan.Rules.Select(r => new RulePlanDto(
            r.RuleId,
            r.DisplayName,
            r.Description,
            r.Tier.ToString(),
            r.Regeneration.Description,
            r.Regeneration.Command,
            r.TotalAllocatedBytes,
            r.TotalFileCount,
            r.Items.Count,
            r.Exclusions.Count,
            [.. r.Items
                .OrderByDescending(i => i.AllocatedBytes)
                .Take(15)
                .Select(i => new PlanItemDto(i.Path, i.AllocatedBytes, i.IsDirectory, i.LastWriteUtc))],
            [.. r.Exclusions
                .Take(10)
                .Select(e => new PlanExclusionDto(e.Path, e.Reason))]))]);
}
