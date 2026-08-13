using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Silt.Core.Attribution;
using Silt.Core.Scanning;

namespace Silt.Core.Snapshots;

/// <summary>Persists and retrieves scan snapshots.</summary>
public interface ISnapshotStore
{
    Snapshot Capture(ScanResult scan, string volumeRoot, long capacityBytes, long freeBytes,
        IReadOnlyList<AppFootprint> apps);
    void Save(Snapshot snapshot);
    IReadOnlyList<SnapshotInfo> List(string volumeRoot);
    Snapshot? Load(string volumeRoot, string id);
    int Prune(string volumeRoot, int keep);
}

/// <summary>
/// Stores snapshots as gzipped JSON under <c>%LOCALAPPDATA%\Silt\snapshots</c>.
/// </summary>
/// <remarks>
/// <para>
/// JSON rather than a database or a packed binary: a snapshot is a few thousand rows written
/// once and read rarely, so the format's cost is irrelevant, while being able to open a
/// history file in a text editor is worth a great deal when a growth figure looks wrong.
/// Gzip brings a typical snapshot to well under a megabyte.
/// </para>
/// <para>
/// This is local history, not telemetry. It never leaves the machine, and it records
/// directory paths and sizes only — never file names or contents.
/// </para>
/// </remarks>
public sealed class SnapshotStore : ISnapshotStore
{
    /// <summary>
    /// Directories smaller than this are omitted. 8 MiB keeps a whole-volume snapshot to a
    /// few thousand rows while retaining everything that could plausibly explain a
    /// gigabyte-scale change.
    /// </summary>
    public const long DefaultEntryFloorBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Directories at or above the floor are always kept; shallower ones are kept
    /// regardless, so the top of the tree stays structurally complete and a diff can always
    /// attribute a change to something.
    /// </summary>
    private const int AlwaysKeepDepth = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _rootDirectory;
    private readonly long _entryFloorBytes;

    public SnapshotStore(string? rootDirectory = null, long entryFloorBytes = DefaultEntryFloorBytes)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Silt",
            "snapshots");
        _entryFloorBytes = entryFloorBytes;
    }

    public Snapshot Capture(
        ScanResult scan,
        string volumeRoot,
        long capacityBytes,
        long freeBytes,
        IReadOnlyList<AppFootprint> apps)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(apps);

        var entries = new List<SnapshotEntry>(4096);
        Collect(scan.Root, depth: 0, entries);

        return new Snapshot(
            Id: NextId(),
            TakenAt: DateTimeOffset.UtcNow,
            VolumeRoot: volumeRoot,
            CapacityBytes: capacityBytes,
            FreeBytes: freeBytes,
            TotalAllocatedBytes: scan.TotalAllocatedBytes,
            TotalFiles: scan.TotalFiles,
            TotalDirectories: scan.TotalDirectories,
            EntryFloorBytes: _entryFloorBytes,
            Directories: entries,
            Apps: [.. apps.Select(a => new SnapshotApp(a.Key, a.DisplayName, a.TotalAllocatedBytes))]);
    }

    private static int _sequence;

    /// <summary>
    /// Builds a snapshot id that sorts by time and cannot collide.
    /// </summary>
    /// <remarks>
    /// The id is also the file name, so a collision silently overwrites history. An earlier
    /// version used second resolution and did exactly that: two scans in the same second
    /// left one snapshot, and the growth report then reported "first recorded scan" forever.
    /// Milliseconds plus a process-wide sequence make the id unique and give a deterministic
    /// order even when two snapshots share a timestamp.
    /// </remarks>
    private static string NextId()
    {
        int sequence = Interlocked.Increment(ref _sequence) & 0xFFFF;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{sequence:x4}");
    }

    private void Collect(ScanNode node, int depth, List<SnapshotEntry> into)
    {
        bool keep = depth <= AlwaysKeepDepth || node.TotalAllocatedBytes >= _entryFloorBytes;
        if (keep)
        {
            into.Add(new SnapshotEntry(node.BuildPath(), node.TotalAllocatedBytes, node.TotalFileCount));
        }

        // A directory below the floor cannot contain one above it, so recursion stops there.
        if (node.TotalAllocatedBytes < _entryFloorBytes && depth > AlwaysKeepDepth)
        {
            return;
        }

        foreach (ScanNode child in node.Children ?? [])
        {
            Collect(child, depth + 1, into);
        }
    }

    public void Save(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string directory = DirectoryFor(snapshot.VolumeRoot);
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, snapshot.Id + ".json.gz");
        using FileStream file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gzip, snapshot, Json);
    }

    public IReadOnlyList<SnapshotInfo> List(string volumeRoot)
    {
        string directory = DirectoryFor(volumeRoot);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<SnapshotInfo>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json.gz"))
        {
            Snapshot? snapshot = ReadFile(file);
            if (snapshot is not null)
            {
                result.Add(new SnapshotInfo(
                    snapshot.Id, snapshot.TakenAt, snapshot.VolumeRoot,
                    snapshot.TotalAllocatedBytes, snapshot.FreeBytes));
            }
        }

        // Ordered by id as well as time so two snapshots sharing a millisecond still have a
        // stable, meaningful order rather than whatever the filesystem happened to return.
        return [.. result
            .OrderByDescending(s => s.TakenAt)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)];
    }

    public Snapshot? Load(string volumeRoot, string id)
    {
        // The id becomes a file name, so it must not be able to escape the directory.
        if (id.AsSpan().ContainsAny('/', '\\', ':') || id.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        string path = Path.Combine(DirectoryFor(volumeRoot), id + ".json.gz");
        return File.Exists(path) ? ReadFile(path) : null;
    }

    /// <summary>Deletes the oldest snapshots, keeping the most recent <paramref name="keep"/>.</summary>
    public int Prune(string volumeRoot, int keep)
    {
        IReadOnlyList<SnapshotInfo> all = List(volumeRoot);
        if (all.Count <= keep)
        {
            return 0;
        }

        string directory = DirectoryFor(volumeRoot);
        int removed = 0;

        foreach (SnapshotInfo old in all.Skip(keep))
        {
            try
            {
                File.Delete(Path.Combine(directory, old.Id + ".json.gz"));
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A snapshot that will not delete is not worth failing a scan over.
            }
        }

        return removed;
    }

    private static Snapshot? ReadFile(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<Snapshot>(gzip, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            // A corrupt or half-written snapshot must not break the history view.
            return null;
        }
    }

    /// <summary>
    /// Maps a volume root to a directory name. <c>C:\</c> becomes <c>C</c>; anything
    /// unexpected is reduced to safe characters so a path can never traverse.
    /// </summary>
    private string DirectoryFor(string volumeRoot)
    {
        var safe = new string([.. volumeRoot.Where(char.IsLetterOrDigit)]);
        return Path.Combine(_rootDirectory, safe.Length == 0 ? "unknown" : safe);
    }
}
