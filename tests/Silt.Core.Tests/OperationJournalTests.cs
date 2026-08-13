using Silt.Core.Cleanup;

namespace Silt.Core.Tests;

public sealed class OperationJournalTests : IDisposable
{
    private readonly string _directory;
    private readonly OperationJournal _journal;

    public OperationJournalTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "silt-journal", Guid.NewGuid().ToString("N"));
        _journal = new OperationJournal(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void AppendOne(string operationId, string path, long bytes, bool succeeded = true) =>
        _journal.Append(operationId, "test.rule", DateTimeOffset.UtcNow,
            [(path, bytes, succeeded, succeeded, succeeded ? null : "failed")]);

    [Fact]
    public void Append_RecordsWhatWasDeleted()
    {
        AppendOne("op-1", @"C:\temp\a.tmp", 1234);

        JournalEntry entry = Assert.Single(_journal.Read());
        Assert.Equal("op-1", entry.OperationId);
        Assert.Equal(@"C:\temp\a.tmp", entry.Path);
        Assert.Equal(1234, entry.Bytes);
        Assert.True(entry.Succeeded);
    }

    [Fact]
    public void Append_ChainsAcrossSeparateCalls()
    {
        AppendOne("op-1", @"C:\temp\a.tmp", 1);
        AppendOne("op-2", @"C:\temp\b.tmp", 2);

        IReadOnlyList<JournalEntry> entries = _journal.Read();

        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.True(_journal.Verify().Intact);
    }

    [Fact]
    public void Verify_PassesOnAnUntouchedJournal()
    {
        for (int i = 0; i < 10; i++)
        {
            AppendOne($"op-{i}", $@"C:\temp\{i}.tmp", i);
        }

        JournalVerification verification = _journal.Verify();

        Assert.True(verification.Intact);
        Assert.Equal(10, verification.EntriesChecked);
    }

    [Fact]
    public void Verify_DetectsAnEditedEntry()
    {
        // The chain cannot prevent tampering by a process running as this user, but it must
        // make tampering visible - that is the achievable property.
        AppendOne("op-1", @"C:\temp\a.tmp", 100);
        AppendOne("op-2", @"C:\temp\b.tmp", 200);

        string[] lines = File.ReadAllLines(_journal.FilePath);
        lines[0] = lines[0].Replace("\"bytes\":100", "\"bytes\":999", StringComparison.Ordinal);
        File.WriteAllLines(_journal.FilePath, lines);

        JournalVerification verification = _journal.Verify();

        Assert.False(verification.Intact);
        Assert.Contains("altered", verification.FirstBreakAt!, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_DetectsARemovedEntry()
    {
        AppendOne("op-1", @"C:\temp\a.tmp", 1);
        AppendOne("op-2", @"C:\temp\b.tmp", 2);
        AppendOne("op-3", @"C:\temp\c.tmp", 3);

        // Deleting the middle line is the obvious way to hide one deletion.
        string[] lines = File.ReadAllLines(_journal.FilePath);
        File.WriteAllLines(_journal.FilePath, [lines[0], lines[2]]);

        Assert.False(_journal.Verify().Intact);
    }

    [Fact]
    public void Read_SurvivesATruncatedFinalLine()
    {
        // An interrupted write leaves a partial line. It must not make the whole record
        // unreadable, or a crash mid-cleanup would destroy the evidence of what happened.
        AppendOne("op-1", @"C:\temp\a.tmp", 1);
        File.AppendAllText(_journal.FilePath, "{\"operationId\":\"op-2\",\"pa");

        Assert.Single(_journal.Read());
    }

    [Fact]
    public void Verify_ReportsIntactForAJournalThatDoesNotExistYet()
    {
        JournalVerification verification = _journal.Verify();

        Assert.True(verification.Intact);
        Assert.Equal(0, verification.EntriesChecked);
    }

    [Fact]
    public void Append_RecordsFailuresAsWellAsSuccesses()
    {
        AppendOne("op-1", @"C:\temp\locked.tmp", 500, succeeded: false);

        JournalEntry entry = Assert.Single(_journal.Read());

        Assert.False(entry.Succeeded);
        Assert.False(entry.Recoverable);
        Assert.Equal("failed", entry.Failure);
    }
}
