using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Silt.Safety;

namespace Silt.Core.Cleanup;

/// <summary>One recorded action.</summary>
public sealed record JournalEntry(
    string OperationId,
    DateTimeOffset At,
    string RuleId,
    string Path,
    long Bytes,
    bool Succeeded,
    bool Recoverable,
    string? Failure,
    string PreviousHash,
    string Hash);

/// <summary>Result of checking the chain.</summary>
public sealed record JournalVerification(bool Intact, int EntriesChecked, string? FirstBreakAt);

/// <summary>
/// An append-only, hash-chained record of everything Silt has deleted.
/// </summary>
/// <remarks>
/// <para>
/// Each entry's hash covers the previous entry's hash, so removing or editing any line
/// breaks every hash after it. That does not make the file tamper-proof — anything running
/// as this user can overwrite it — but it makes tampering <b>detectable</b>, which is the
/// achievable property and the one that matters when reconstructing what happened.
/// </para>
/// <para>
/// Writes are guarded by <see cref="PathJail.Require"/> against the journal's own directory.
/// This type is on the CI mutation gate's exemption list, and that exemption is only honest
/// because the constraint is enforced here rather than asserted in a comment.
/// </para>
/// <para>
/// Local only. It never leaves the machine and records paths and sizes, never file contents.
/// </para>
/// </remarks>
public sealed class OperationJournal
{
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _path;

    public OperationJournal(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Silt");
        _path = Path.Combine(_directory, "operations.jsonl");
    }

    /// <summary>Where the journal is written.</summary>
    public string FilePath => _path;

    /// <summary>Appends one entry per outcome, chained to whatever is already recorded.</summary>
    public IReadOnlyList<JournalEntry> Append(
        string operationId,
        string ruleId,
        DateTimeOffset at,
        IEnumerable<(string Path, long Bytes, bool Succeeded, bool Recoverable, string? Failure)> outcomes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(outcomes);

        var written = new List<JournalEntry>();

        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            PathJail.Require(_directory, _path, "append to the operation journal");

            string previous = LastHashUnlocked();
            var builder = new StringBuilder();

            foreach ((string path, long bytes, bool succeeded, bool recoverable, string? failure) in outcomes)
            {
                var entry = new JournalEntry(
                    operationId, at, ruleId, path, bytes, succeeded, recoverable, failure,
                    previous, Hash: string.Empty);

                string hash = ComputeHash(entry);
                entry = entry with { Hash = hash };
                previous = hash;

                builder.AppendLine(JsonSerializer.Serialize(entry, Json));
                written.Add(entry);
            }

            if (builder.Length > 0)
            {
                File.AppendAllText(_path, builder.ToString(), Encoding.UTF8);
            }
        }

        return written;
    }

    public IReadOnlyList<JournalEntry> Read()
    {
        lock (_gate)
        {
            return ReadUnlocked();
        }
    }

    /// <summary>Walks the chain and reports the first entry whose hash does not follow.</summary>
    public JournalVerification Verify()
    {
        IReadOnlyList<JournalEntry> entries = Read();
        string previous = GenesisHash;

        for (int i = 0; i < entries.Count; i++)
        {
            JournalEntry entry = entries[i];

            if (entry.PreviousHash != previous)
            {
                return new JournalVerification(false, i, $"entry {i}: chain does not follow");
            }

            string expected = ComputeHash(entry with { Hash = string.Empty });
            if (expected != entry.Hash)
            {
                return new JournalVerification(false, i, $"entry {i}: contents were altered");
            }

            previous = entry.Hash;
        }

        return new JournalVerification(true, entries.Count, null);
    }

    private List<JournalEntry> ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var entries = new List<JournalEntry>();
        foreach (string line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<JournalEntry>(line, Json) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A truncated final line from an interrupted write is expected; it must not
                // make the whole journal unreadable.
            }
        }

        return entries;
    }

    private string LastHashUnlocked()
    {
        List<JournalEntry> entries = ReadUnlocked();
        return entries.Count == 0 ? GenesisHash : entries[^1].Hash;
    }

    /// <summary>
    /// Hashes an entry over a fixed field order, so the digest does not depend on how the
    /// serializer happened to order properties.
    /// </summary>
    private static string ComputeHash(JournalEntry entry)
    {
        string canonical = string.Create(CultureInfo.InvariantCulture,
            $"{entry.PreviousHash}|{entry.OperationId}|{entry.At:O}|{entry.RuleId}|{entry.Path}|" +
            $"{entry.Bytes}|{entry.Succeeded}|{entry.Recoverable}|{entry.Failure}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
