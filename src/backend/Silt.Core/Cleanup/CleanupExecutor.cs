using Silt.Safety;

namespace Silt.Core.Cleanup;

/// <summary>Why a batch was refused before anything was touched.</summary>
public enum RefusalReason
{
    None,
    /// <summary>The batch is larger than the Recycle Bin will hold, so part of it would be destroyed.</summary>
    ExceedsRecycleBinCapacity,
    /// <summary>The Recycle Bin quota could not be read, so recoverability cannot be promised.</summary>
    RecycleBinCapacityUnknown,
    /// <summary>Nothing in the plan survived re-validation.</summary>
    NothingToDo,
}

/// <summary>The outcome of executing one rule's plan.</summary>
public sealed record ExecutionResult(
    string OperationId,
    string RuleId,
    bool Executed,
    RefusalReason Refusal,
    string? RefusalMessage,
    int ItemsDeleted,
    int ItemsFailed,
    long BytesDeleted,
    IReadOnlyList<DeletionOutcome> Outcomes,
    long RecycleBinAvailableBytes);

/// <summary>
/// Executes a reviewed plan, or refuses to.
/// </summary>
/// <remarks>
/// <para>
/// The executor's most important behaviour is refusal. A batch larger than the Recycle Bin
/// quota is <b>rejected before anything is touched</b> and the user is told to split it,
/// because the shell's response to an oversized delete is not to fail but to permanently
/// destroy the overflow and report success.
/// </para>
/// <para>
/// Everything that does proceed goes through <see cref="SandboxedFileSystem"/>, which
/// re-checks the denylist and re-validates each item against what the plan recorded. There
/// is no path in this type that deletes permanently.
/// </para>
/// </remarks>
public sealed class CleanupExecutor(
    Denylist denylist,
    OperationJournal? journal = null,
    SandboxedFileSystem? fileSystem = null,
    IRecycleBinProbe? recycleBin = null)
{
    private readonly SandboxedFileSystem _fileSystem = fileSystem ?? new SandboxedFileSystem(denylist);
    private readonly OperationJournal _journal = journal ?? new OperationJournal();
    private readonly IRecycleBinProbe _recycleBin = recycleBin ?? new RecycleBinProbe();

    /// <summary>
    /// Executes one rule's portion of a plan.
    /// </summary>
    /// <param name="operationId">
    /// Correlates journal entries. Supplied by the caller so the id in the audit trail is
    /// the same one the user was shown.
    /// </param>
    public ExecutionResult Execute(
        RulePlan rulePlan,
        string operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rulePlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        if (rulePlan.Items.Count == 0)
        {
            return Refuse(operationId, rulePlan, RefusalReason.NothingToDo,
                "This plan has nothing to remove.", 0);
        }

        // Capacity is judged against the volume the items actually live on.
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(rulePlan.Items[0].Path))
                            ?? throw new InvalidOperationException("Plan item has no volume root.");

        RecycleBinState bin = _recycleBin.Query(volumeRoot);

        if (!bin.CapacityKnown)
        {
            return Refuse(operationId, rulePlan, RefusalReason.RecycleBinCapacityUnknown,
                "Silt could not read the Recycle Bin quota for this volume, so it cannot " +
                "promise these items would be recoverable. Nothing was deleted.",
                bin.AvailableBytes);
        }

        if (rulePlan.TotalAllocatedBytes > bin.AvailableBytes)
        {
            return Refuse(operationId, rulePlan, RefusalReason.ExceedsRecycleBinCapacity,
                $"This batch is {Format(rulePlan.TotalAllocatedBytes)} but only " +
                $"{Format(bin.AvailableBytes)} will fit in the Recycle Bin. Windows would " +
                "permanently destroy the overflow rather than fail, so nothing was deleted. " +
                "Empty the Recycle Bin or clean this in smaller batches.",
                bin.AvailableBytes);
        }

        // Snapshot what the plan recorded, so each item can be checked against it at the
        // moment of deletion rather than trusted from earlier.
        Dictionary<string, PlanItem> planned =
            rulePlan.Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<DeletionOutcome> outcomes = _fileSystem.RecycleAll(
            [.. planned.Keys],
            path => Revalidate(path, planned),
            cancellationToken);

        long bytesDeleted = outcomes
            .Where(o => o.Succeeded)
            .Sum(o => planned.TryGetValue(o.Path, out PlanItem? item) ? item.AllocatedBytes : 0);

        _journal.Append(
            operationId,
            rulePlan.RuleId,
            now,
            outcomes.Select(o => (
                o.Path,
                planned.TryGetValue(o.Path, out PlanItem? item) ? item.AllocatedBytes : 0,
                o.Succeeded,
                o.WentToRecycleBin,
                o.Failure)));

        return new ExecutionResult(
            operationId,
            rulePlan.RuleId,
            Executed: true,
            RefusalReason.None,
            null,
            outcomes.Count(o => o.Succeeded),
            outcomes.Count(o => !o.Succeeded),
            bytesDeleted,
            outcomes,
            bin.AvailableBytes);
    }

    /// <summary>
    /// Confirms an item still matches what the user reviewed.
    /// </summary>
    /// <remarks>
    /// A file that grew, shrank, or was rewritten since planning is not the file that was
    /// approved. Deleting it anyway would mean the reviewed plan and the executed action
    /// were about different things.
    /// </remarks>
    private static string? Revalidate(string path, Dictionary<string, PlanItem> planned)
    {
        if (!planned.TryGetValue(path, out PlanItem? item))
        {
            return "This item was not part of the reviewed plan.";
        }

        try
        {
            if (item.IsDirectory)
            {
                if (!Directory.Exists(path))
                {
                    return "The folder no longer exists.";
                }
                return null;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return "The file no longer exists.";
            }
            if (info.Length != item.AllocatedBytes && item.AllocatedBytes > 0)
            {
                return "The file changed size after the plan was reviewed.";
            }
            if (Math.Abs((info.LastWriteTimeUtc - item.LastWriteUtc.UtcDateTime).TotalSeconds) > 2)
            {
                return "The file was modified after the plan was reviewed.";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not be re-checked before deletion: {ex.Message}";
        }
    }

    private static ExecutionResult Refuse(
        string operationId, RulePlan plan, RefusalReason reason, string message, long available) =>
        new(operationId, plan.RuleId, false, reason, message, 0, 0, 0, [], available);

    private static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:F2} {units[unit]}";
    }
}
