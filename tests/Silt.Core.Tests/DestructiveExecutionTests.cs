using Silt.Core.Cleanup;
using Silt.Core.Safety;
using Silt.Safety;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

/// <summary>
/// Tests that genuinely delete files.
/// </summary>
/// <remarks>
/// <para>
/// Four independent interlocks, because a destructive test that runs by accident is worse
/// than no test:
/// </para>
/// <list type="number">
/// <item><c>[Trait("Category","Destructive")]</c>, excluded from every default run and from CI.</item>
/// <item><c>SILT_DESTRUCTIVE_TESTS=1</c> must be set explicitly.</item>
/// <item>Every target is created by the test inside its own scratch directory under TEMP.</item>
/// <item><see cref="SandboxedFileSystem"/> re-checks the denylist on every item regardless.</item>
/// </list>
/// <para>
/// Run deliberately:
/// <code>$env:SILT_DESTRUCTIVE_TESTS=1; dotnet test --filter "Category=Destructive"</code>
/// </para>
/// <para>
/// Items are sent to the real Recycle Bin, which is the point — the promise being verified
/// is recoverability, and only the real shell can demonstrate it. The files involved are a
/// few kilobytes.
/// </para>
/// </remarks>
public sealed class DestructiveExecutionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "silt-destructive", Guid.NewGuid().ToString("N"));

    private readonly string _journalDir =
        Path.Combine(Path.GetTempPath(), "silt-destructive-journal", Guid.NewGuid().ToString("N"));

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SILT_DESTRUCTIVE_TESTS") == "1";

    public void Dispose()
    {
        foreach (string directory in new[] { _root, _journalDir })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private string Scratch(string name, int bytes = 2048)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        return path;
    }

    private static RulePlan PlanFor(params string[] paths) => new(
        "destructive.test", "Destructive test", "Deletes scratch files.", SafetyTier.AlwaysSafe,
        new Regeneration("Recreated by the test."),
        [.. paths.Select(p => new PlanItem(
            p, new FileInfo(p).Length, false, new FileInfo(p).LastWriteTimeUtc, 1))],
        paths.Sum(p => new FileInfo(p).Length), paths.Length, [], []);

    [Fact]
    [Trait("Category", "Destructive")]
    public void Execute_SendsFilesToTheRecycleBinAndRecordsThem()
    {
        if (!Enabled)
        {
            output.WriteLine("SKIPPED - set SILT_DESTRUCTIVE_TESTS=1 to run.");
            return;
        }

        string a = Scratch("scratch-a.tmp");
        string b = Scratch("scratch-b.tmp");

        string volumeRoot = Path.GetPathRoot(a)!;
        RecycleBinState before = RecycleBinCapacity.Query(volumeRoot);

        var journal = new OperationJournal(_journalDir);
        var executor = new CleanupExecutor(WindowsProtectedPaths.BuildDenylist(), journal);

        ExecutionResult result = executor.Execute(PlanFor(a, b), "destructive-1", DateTimeOffset.UtcNow);

        output.WriteLine($"executed={result.Executed} refusal={result.Refusal} {result.RefusalMessage}");
        output.WriteLine($"deleted={result.ItemsDeleted} failed={result.ItemsFailed}");

        Assert.True(result.Executed, result.RefusalMessage);
        Assert.Equal(2, result.ItemsDeleted);
        Assert.Equal(0, result.ItemsFailed);

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));

        // Gone is not enough: the promise is that they are recoverable, so the bin must
        // actually have grown.
        RecycleBinState after = RecycleBinCapacity.Query(volumeRoot);
        output.WriteLine($"recycle bin items {before.CurrentItems} -> {after.CurrentItems}");
        Assert.True(after.CurrentItems > before.CurrentItems,
            "Files vanished without reaching the Recycle Bin, so they are not recoverable.");

        Assert.All(result.Outcomes, o => Assert.True(o.WentToRecycleBin));

        IReadOnlyList<JournalEntry> entries = journal.Read();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.Recoverable));
        Assert.True(journal.Verify().Intact);
    }

    [Fact]
    [Trait("Category", "Destructive")]
    public void Execute_RefusesAnItemThatChangedSinceThePlanWasReviewed()
    {
        if (!Enabled)
        {
            output.WriteLine("SKIPPED - set SILT_DESTRUCTIVE_TESTS=1 to run.");
            return;
        }

        string stable = Scratch("stable.tmp");
        string modified = Scratch("modified.tmp");

        RulePlan plan = PlanFor(stable, modified);

        // Something rewrites the file between review and execution. It is no longer the item
        // the user approved, so it must be left alone rather than deleted on the assumption
        // nothing moved.
        File.WriteAllBytes(modified, new byte[9000]);

        var executor = new CleanupExecutor(
            WindowsProtectedPaths.BuildDenylist(), new OperationJournal(_journalDir));

        ExecutionResult result = executor.Execute(plan, "destructive-2", DateTimeOffset.UtcNow);

        Assert.True(result.Executed, result.RefusalMessage);
        Assert.False(File.Exists(stable));
        Assert.True(File.Exists(modified), "A file that changed after review was deleted anyway.");

        DeletionOutcome rejected = Assert.Single(result.Outcomes, o => !o.Succeeded);
        Assert.Contains("changed size", rejected.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Destructive")]
    public void Execute_WillNotDeleteAProtectedPathEvenWhenHandedOneDirectly()
    {
        if (!Enabled)
        {
            output.WriteLine("SKIPPED - set SILT_DESTRUCTIVE_TESTS=1 to run.");
            return;
        }

        // A plan is constructed by hand pointing at the user's Documents folder, bypassing
        // the planner entirely. The funnel must still refuse it.
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string target = Path.Combine(documents, "silt-should-never-touch-this.txt");

        var sandbox = new SandboxedFileSystem(WindowsProtectedPaths.BuildDenylist());
        IReadOnlyList<DeletionOutcome> outcomes = sandbox.RecycleAll([target], _ => null);

        DeletionOutcome outcome = Assert.Single(outcomes);
        Assert.False(outcome.Succeeded);
        Assert.Contains("safety list", outcome.Failure!, StringComparison.OrdinalIgnoreCase);

        output.WriteLine($"refused as expected: {outcome.Failure}");
    }
}
