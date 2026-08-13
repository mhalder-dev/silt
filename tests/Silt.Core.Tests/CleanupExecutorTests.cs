using Silt.Core.Cleanup;
using Silt.Safety;

namespace Silt.Core.Tests;

/// <summary>
/// Executor behaviour that does not require deleting anything.
/// </summary>
/// <remarks>
/// Refusal is the executor's most important property, so it is tested here with an injected
/// Recycle Bin probe rather than left to whatever state the developer's bin happens to be
/// in. Tests that actually delete live in <c>DestructiveExecutionTests</c> behind an
/// explicit opt-in.
/// </remarks>
public sealed class CleanupExecutorTests : IDisposable
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    private readonly string _root;
    private readonly string _journalDir;

    public CleanupExecutorTests()
    {
        string id = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), "silt-exec", id);
        _journalDir = Path.Combine(Path.GetTempPath(), "silt-journal", id);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_journalDir);
    }

    public void Dispose()
    {
        foreach (string directory in new[] { _root, _journalDir })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeBin(long available, bool known = true) : IRecycleBinProbe
    {
        public RecycleBinState Query(string volumeRoot) =>
            new(volumeRoot, CurrentBytes: 0, CurrentItems: 0,
                MaxCapacityBytes: available, CapacityKnown: known);
    }

    private RulePlan PlanWith(params (string Name, long Bytes)[] items)
    {
        var planItems = new List<PlanItem>();
        foreach ((string name, long bytes) in items)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[Math.Min(bytes, 4096)]);
            planItems.Add(new PlanItem(path, bytes, false, DateTimeOffset.UtcNow.AddDays(-30), 1));
        }

        return new RulePlan(
            "test.rule", "Test rule", "For tests.", SafetyTier.AlwaysSafe,
            new Regeneration("Regenerated."), planItems,
            planItems.Sum(i => i.AllocatedBytes), planItems.Count, [], []);
    }

    private CleanupExecutor ExecutorWith(IRecycleBinProbe bin) =>
        new(new Denylist([]), new OperationJournal(_journalDir), fileSystem: null, recycleBin: bin);

    [Fact]
    public void Execute_RefusesABatchLargerThanTheRecycleBinWillHold()
    {
        // The behaviour that matters most. Windows does not fail an oversized recycle - it
        // permanently destroys the overflow and reports success - so the batch must be
        // stopped before anything is touched.
        RulePlan plan = PlanWith(("big.tmp", 40 * Gib));

        ExecutionResult result = ExecutorWith(new FakeBin(available: 20 * Gib))
            .Execute(plan, "op-1", DateTimeOffset.UtcNow);

        Assert.False(result.Executed);
        Assert.Equal(RefusalReason.ExceedsRecycleBinCapacity, result.Refusal);
        Assert.Equal(0, result.ItemsDeleted);

        // And nothing was touched.
        Assert.True(File.Exists(Path.Combine(_root, "big.tmp")));
    }

    [Fact]
    public void Execute_ExplainsTheRefusalInTermsTheUserCanAct_On()
    {
        RulePlan plan = PlanWith(("big.tmp", 40 * Gib));

        ExecutionResult result = ExecutorWith(new FakeBin(available: 20 * Gib))
            .Execute(plan, "op-1", DateTimeOffset.UtcNow);

        Assert.Contains("permanently destroy", result.RefusalMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smaller batches", result.RefusalMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_RefusesWhenTheRecycleBinQuotaCannotBeRead()
    {
        // Without a known quota there is no way to promise recoverability, and a guess could
        // authorize an unrecoverable deletion.
        RulePlan plan = PlanWith(("a.tmp", 1 * Mib));

        ExecutionResult result = ExecutorWith(new FakeBin(available: 0, known: false))
            .Execute(plan, "op-2", DateTimeOffset.UtcNow);

        Assert.False(result.Executed);
        Assert.Equal(RefusalReason.RecycleBinCapacityUnknown, result.Refusal);
        Assert.True(File.Exists(Path.Combine(_root, "a.tmp")));
    }

    [Fact]
    public void Execute_RefusesAnEmptyPlanWithoutTouchingTheBin()
    {
        var empty = new RulePlan(
            "test.rule", "Test rule", "For tests.", SafetyTier.AlwaysSafe,
            new Regeneration("Regenerated."), [], 0, 0, [], []);

        ExecutionResult result = ExecutorWith(new FakeBin(available: 100 * Gib))
            .Execute(empty, "op-3", DateTimeOffset.UtcNow);

        Assert.False(result.Executed);
        Assert.Equal(RefusalReason.NothingToDo, result.Refusal);
    }

    [Fact]
    public void Execute_RefusalIsRecordedNowhereBecauseNothingHappened()
    {
        RulePlan plan = PlanWith(("big.tmp", 40 * Gib));
        var journal = new OperationJournal(_journalDir);

        new CleanupExecutor(new Denylist([]), journal, null, new FakeBin(20 * Gib))
            .Execute(plan, "op-4", DateTimeOffset.UtcNow);

        // A refusal is not an operation. Journalling it would pollute the record of what was
        // actually deleted with things that never were.
        Assert.Empty(journal.Read());
    }
}
