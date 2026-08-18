using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Silt.Core.Native;
using Silt.Core.Scanning;

namespace Silt.Core.Duplicates;

/// <summary>
/// Finds sets of files with identical content beneath a directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type never deletes anything and has no path to deletion.</b> It reports. Acting
/// on a group goes through the ordinary cleanup planner and <c>SandboxedFileSystem</c>, with
/// the same dry-run and re-validation every other deletion gets. That separation is
/// deliberate: "these two files are identical" and "it is safe to remove one of them" are
/// different claims, and only the first one is a measurement.
/// </para>
/// <para>
/// The search is a funnel, and the ordering of its stages is the whole performance story.
/// The naive implementation hashes every file, which on the machine in §1.2 is roughly
/// 900 GB of reads for an answer that needs about one percent of that. Instead:
/// </para>
/// <list type="number">
///   <item>Group by exact logical size, from directory metadata alone. No file is opened.
///         A file with a unique size cannot have a duplicate, and on a real profile this
///         eliminates the overwhelming majority.</item>
///   <item>Collapse hardlinks by file id. Additional links to one file are the same bytes
///         on disk; reporting them would promise reclaimable space that does not exist.</item>
///   <item>Hash the first 4 KiB. Files that differ at all almost always differ early - a
///         header, a magic number, a timestamp - so this kills most survivors for one
///         cluster's worth of reading each.</item>
///   <item>Hash in full, but only what survived. For files at or below the head sample the
///         head hash already <em>is</em> the full hash, so they are not read twice.</item>
///   <item>Compare bytes, if <see cref="DuplicateOptions.VerifyByteForByte"/> is on.</item>
/// </list>
/// <para>
/// Logical size, not allocated size, is the grouping key. Two identical files can have
/// different allocation if one is NTFS-compressed and the other is not, and grouping on
/// allocation would silently miss exactly that pair.
/// </para>
/// </remarks>
public sealed class DuplicateFinder : IDuplicateFinder
{
    /// <summary>
    /// Bytes read for the cheap discriminating hash. One cluster: a smaller figure would
    /// cost the same, since the filesystem hands over a whole cluster regardless.
    /// </summary>
    internal const int HeadSampleBytes = 4096;

    private const int ReadBufferBytes = 1 << 20;

    public DuplicateResult Find(DuplicateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();

        string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        if (rootPath.Length == 2 && rootPath[1] == ':')
        {
            rootPath += Path.DirectorySeparatorChar; // "C:" means the current dir on C:, not its root
        }

        WalkResult walk = Enumerate(rootPath, options, cancellationToken);

        long hardLinksCollapsed = 0;
        long deniedSkipped = 0;
        var candidates = new List<int>();

        // Stage 1 - size. Metadata only; nothing has been opened yet.
        foreach (List<int> bucket in BucketBySize(walk, Enumerable.Range(0, walk.Files.Count)))
        {
            // Stage 2 - hardlinks. Done here rather than at enumeration time because a file
            // id only matters once something else shares its size; a global id set over
            // every file on the volume would cost memory for no extra answer.
            var seenIds = new HashSet<long>(bucket.Count);
            var distinct = new List<int>(bucket.Count);
            foreach (int index in bucket)
            {
                if (seenIds.Add(walk.Files[index].FileId))
                {
                    distinct.Add(index);
                }
                else
                {
                    hardLinksCollapsed++;
                }
            }

            if (distinct.Count < 2)
            {
                continue;
            }

            // The denylist check is a path split plus several string scans per file, so it
            // runs here - over the few thousand files that share a size with something -
            // rather than over the million that were enumerated.
            if (options.Denylist is not null)
            {
                var allowed = new List<int>(distinct.Count);
                foreach (int index in distinct)
                {
                    if (options.Denylist.Check(walk.PathOf(index)).IsDenied)
                    {
                        deniedSkipped++;
                    }
                    else
                    {
                        allowed.Add(index);
                    }
                }

                distinct = allowed;
                if (distinct.Count < 2)
                {
                    continue;
                }
            }

            candidates.AddRange(distinct);
        }

        var counters = new HashCounters { CandidateFiles = candidates.Count };

        // Stages 3 and 4 - head hash, then full hash, each narrowing the next.
        List<List<int>> sized = BucketBySize(walk, candidates);
        List<List<int>> headGroups =
            Refine(sized, walk, options, counters, HeadSampleBytes, cancellationToken);
        List<List<int>> fullGroups =
            Refine(headGroups, walk, options, counters, long.MaxValue, cancellationToken);

        // Stage 5 - bytes. Parallel across groups for the same reason the hash stages are:
        // measured on this machine's LocalAppData, verification is roughly half of everything
        // the search reads, and running it on one thread made it roughly half the wall clock
        // while every other core sat idle. See docs/PLAN.md §5i.
        var confirmedGroups = new ConcurrentBag<DuplicateGroup>();

        Parallel.ForEach(
            fullGroups,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.DegreeOfParallelism),
                CancellationToken = cancellationToken,
            },
            group =>
            {
                List<int> confirmed = options.VerifyByteForByte
                    ? Verify(group, walk, counters, cancellationToken)
                    : group;

                if (confirmed.Count < 2)
                {
                    return;
                }

                var paths = new List<string>(confirmed.Count);
                foreach (int index in confirmed)
                {
                    paths.Add(walk.PathOf(index));
                }

                paths.Sort(static (a, b) =>
                {
                    int byLength = a.Length.CompareTo(b.Length);
                    return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
                });

                confirmedGroups.Add(new DuplicateGroup
                {
                    SizeBytes = walk.Files[confirmed[0]].SizeBytes,
                    Paths = paths,
                });
            });

        var groups = new List<DuplicateGroup>(confirmedGroups);
        long reclaimable = 0;
        foreach (DuplicateGroup group in groups)
        {
            reclaimable += group.ReclaimableBytes;
        }

        // Tie-broken on the first path, not left to whatever order the bag hands back.
        // Without it two runs over an unchanged tree produce differently ordered reports,
        // which makes the output impossible to diff and looks like the tree changed.
        groups.Sort(static (a, b) =>
        {
            int byReclaimable = b.ReclaimableBytes.CompareTo(a.ReclaimableBytes);
            return byReclaimable != 0 ? byReclaimable : string.CompareOrdinal(a.Paths[0], b.Paths[0]);
        });

        sw.Stop();

        return new DuplicateResult
        {
            Groups = groups,
            Duration = sw.Elapsed,
            TotalReclaimableBytes = reclaimable,
            FilesExamined = walk.FilesExamined,
            CandidateFiles = candidates.Count,
            BytesRead = counters.BytesRead,
            HardLinksCollapsed = hardLinksCollapsed,
            CloudPlaceholdersSkipped = walk.CloudPlaceholdersSkipped,
            DeniedFilesSkipped = deniedSkipped,
            AccessDeniedCount = walk.AccessDeniedCount,
            UnreadableFileCount = counters.Unreadable,
        };
    }

    /// <summary>Groups file indices by exact logical size, keeping only shared sizes.</summary>
    private static List<List<int>> BucketBySize(WalkResult walk, IEnumerable<int> indices)
    {
        var bySize = new Dictionary<long, List<int>>();
        foreach (int index in indices)
        {
            long size = walk.Files[index].SizeBytes;
            if (!bySize.TryGetValue(size, out List<int>? bucket))
            {
                bucket = [];
                bySize[size] = bucket;
            }

            bucket.Add(index);
        }

        var shared = new List<List<int>>();
        foreach (List<int> bucket in bySize.Values)
        {
            if (bucket.Count >= 2)
            {
                shared.Add(bucket);
            }
        }

        return shared;
    }

    /// <summary>
    /// Splits each group by the hash of its members' first <paramref name="hashBytes"/>
    /// bytes, discarding anything left alone.
    /// </summary>
    private static List<List<int>> Refine(
        IReadOnlyList<List<int>> groups,
        WalkResult walk,
        DuplicateOptions options,
        HashCounters counters,
        long hashBytes,
        CancellationToken cancellationToken)
    {
        bool IsAlreadyFullyHashed(List<int> group) =>
            hashBytes == long.MaxValue && walk.Files[group[0]].SizeBytes <= HeadSampleBytes;

        // Work is flattened before it is parallelized. Parallelizing over groups instead
        // would leave every worker but one idle whenever one group is far larger than the
        // rest, which is the normal shape: a few hundred copies of one installer and a long
        // tail of pairs.
        var work = new List<int>();
        foreach (List<int> group in groups)
        {
            // A file at or below the sample size was already hashed in full by the head
            // pass, so a second read cannot separate it from anything. On a tree of small
            // files that saved read is most of the total cost.
            if (IsAlreadyFullyHashed(group))
            {
                continue;
            }

            work.AddRange(group);
        }

        var hashes = new ConcurrentDictionary<int, string>();

        if (work.Count > 0)
        {
            Parallel.ForEach(
                work,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, options.DegreeOfParallelism),
                    CancellationToken = cancellationToken,
                },
                fileIndex =>
                {
                    string? hash = TryHash(walk.PathOf(fileIndex), hashBytes, counters);
                    if (hash is not null)
                    {
                        hashes[fileIndex] = hash;
                    }

                    long done = Interlocked.Increment(ref counters.FilesHashedRef);
                    if (options.Progress is not null && done % 64 == 0)
                    {
                        options.Progress.Report(new DuplicateProgress(
                            done,
                            counters.CandidateFiles,
                            Interlocked.Read(ref counters.BytesReadRef)));
                    }
                });
        }

        var refined = new List<List<int>>();
        foreach (List<int> group in groups)
        {
            if (IsAlreadyFullyHashed(group))
            {
                refined.Add(group);
                continue;
            }

            var byHash = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int index in group)
            {
                // A file that could not be read is dropped rather than grouped with the
                // others. It is counted in UnreadableFileCount so the omission is visible.
                if (!hashes.TryGetValue(index, out string? hash))
                {
                    continue;
                }

                if (!byHash.TryGetValue(hash, out List<int>? bucket))
                {
                    bucket = [];
                    byHash[hash] = bucket;
                }

                bucket.Add(index);
            }

            foreach (List<int> bucket in byHash.Values)
            {
                if (bucket.Count >= 2)
                {
                    refined.Add(bucket);
                }
            }
        }

        return refined;
    }

    private static string? TryHash(string path, long hashBytes, HashCounters counters)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(hashBytes, ReadBufferBytes));

        try
        {
            using FileStream stream = OpenForRead(path);
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            long remaining = hashBytes;
            long read = 0;
            while (remaining > 0)
            {
                int want = (int)Math.Min(remaining, buffer.Length);
                int got = stream.Read(buffer, 0, want);
                if (got == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, got);
                remaining -= got;
                read += got;
            }

            Interlocked.Add(ref counters.BytesReadRef, read);
            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref counters.UnreadableRef);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Confirms a hash-matched group byte for byte, comparing each member against the first.
    /// </summary>
    /// <remarks>
    /// Against the first member rather than pairwise: content identity is transitive, so
    /// n-1 comparisons settle a group of n where pairwise would cost n(n-1)/2 reads to learn
    /// the same thing. A member that disagrees is dropped rather than splitting the group -
    /// a disagreement here means either a SHA-256 collision or a file that changed
    /// mid-search, and neither is something to report as a duplicate of anything.
    /// </remarks>
    private static List<int> Verify(
        List<int> group,
        WalkResult walk,
        HashCounters counters,
        CancellationToken cancellationToken)
    {
        var confirmed = new List<int> { group[0] };

        for (int i = 1; i < group.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ContentsEqual(walk.PathOf(group[0]), walk.PathOf(group[i]), counters))
            {
                confirmed.Add(group[i]);
            }
        }

        return confirmed;
    }

    private static bool ContentsEqual(string left, string right, HashCounters counters)
    {
        byte[] leftBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        byte[] rightBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);

        try
        {
            using FileStream a = OpenForRead(left);
            using FileStream b = OpenForRead(right);

            if (a.Length != b.Length)
            {
                return false;
            }

            while (true)
            {
                // ReadAtLeast, not Read: a single Read may return a short buffer for reasons
                // that have nothing to do with content, and comparing two short reads of
                // different lengths would report identical files as different.
                int readA = a.ReadAtLeast(leftBuffer, leftBuffer.Length, throwOnEndOfStream: false);
                int readB = b.ReadAtLeast(rightBuffer, rightBuffer.Length, throwOnEndOfStream: false);

                Interlocked.Add(ref counters.BytesReadRef, readA + readB);

                if (readA != readB)
                {
                    return false;
                }

                if (readA == 0)
                {
                    return true;
                }

                if (!leftBuffer.AsSpan(0, readA).SequenceEqual(rightBuffer.AsSpan(0, readB)))
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref counters.UnreadableRef);
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    private static FileStream OpenForRead(string path) => new(
        BfsScanner.ToExtendedPath(path),
        FileMode.Open,
        FileAccess.Read,
        // FileShare.Delete alongside ReadWrite: without it a file some other process holds
        // open with delete intent cannot be opened at all, and the search would report it
        // unreadable rather than reading the bytes that are still perfectly there.
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 0,
        FileOptions.SequentialScan);

    private static unsafe WalkResult Enumerate(
        string rootPath,
        DuplicateOptions options,
        CancellationToken cancellationToken)
    {
        var result = new WalkResult();
        result.Directories.Add(rootPath);

        var pending = new Stack<int>();
        pending.Push(0);

        // One native buffer for the whole walk. Allocating per directory would dominate the
        // cost of enumerating small ones - the same finding that shaped BfsScanner.
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(DirectoryEnumerator.BufferSize, 16);
        var subdirs = new List<string>(64);

        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int dirIndex = pending.Pop();
                subdirs.Clear();

                var sink = new FileSink
                {
                    SubDirs = subdirs,
                    Files = result.Files,
                    DirIndex = dirIndex,
                    MinimumSize = options.MinimumFileSize,
                };

                EnumerateStatus status = DirectoryEnumerator.Enumerate(
                    BfsScanner.ToExtendedPath(result.Directories[dirIndex]),
                    buffer,
                    ref sink,
                    out _);

                result.FilesExamined += sink.Examined;
                result.CloudPlaceholdersSkipped += sink.CloudSkipped;

                if (status == EnumerateStatus.AccessDenied)
                {
                    result.AccessDeniedCount++;
                    continue;
                }

                foreach (string name in subdirs)
                {
                    result.Directories.Add(Path.Combine(result.Directories[dirIndex], name));
                    pending.Push(result.Directories.Count - 1);
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }

        return result;
    }

    /// <summary>
    /// A candidate file. The directory is an index into a shared list rather than a string.
    /// </summary>
    /// <remarks>
    /// The scan tree stores directories only, so a duplicate search has to hold a per-file
    /// record of its own, and over a whole volume that is millions of them. A full directory
    /// path per record measured at roughly 200 bytes each on <c>ScanNode</c> - the single
    /// largest component of the retained tree, which is why it was dropped there. An index
    /// costs four.
    /// </remarks>
    private readonly record struct CandidateFile(int DirIndex, string Name, long SizeBytes, long FileId);

    private sealed class WalkResult
    {
        public List<string> Directories { get; } = [];

        public List<CandidateFile> Files { get; } = [];

        public long FilesExamined { get; set; }

        public long CloudPlaceholdersSkipped { get; set; }

        public int AccessDeniedCount { get; set; }

        public string PathOf(int fileIndex)
        {
            CandidateFile file = Files[fileIndex];
            return Path.Combine(Directories[file.DirIndex], file.Name);
        }
    }

    private sealed class HashCounters
    {
        public long FilesHashedRef;
        public long BytesReadRef;
        public int UnreadableRef;

        public int CandidateFiles { get; init; }

        public long BytesRead => BytesReadRef;

        public int Unreadable => UnreadableRef;
    }

    /// <summary>Per-directory accumulator. A struct so the enumerator can devirtualize it.</summary>
    private struct FileSink : IEntrySink
    {
        public List<string> SubDirs;
        public List<CandidateFile> Files;
        public int DirIndex;
        public long MinimumSize;

        public long Examined;
        public long CloudSkipped;

        public void OnEntry(ReadOnlySpan<char> name, in FileIdBothDirInfo info)
        {
            if (info.IsDirectory)
            {
                // Junctions and symlinks are not descended: their content lives elsewhere
                // and is either reached by its real path or outside the search entirely.
                // Following them reports the same bytes twice and can loop forever.
                if (!info.IsReparsePoint || !ReparseTags.IsNameSurrogate(info.ReparseTag))
                {
                    SubDirs.Add(name.ToString());
                }

                return;
            }

            Examined++;

            // A file reparse point is a link, not storage.
            if (info.IsReparsePoint && ReparseTags.IsNameSurrogate(info.ReparseTag))
            {
                return;
            }

            // Cloud-tiered content is skipped WITHOUT BEING OPENED. Opening a placeholder
            // hydrates it - OneDrive downloads the whole file - so a duplicate search over a
            // synced profile would quietly pull gigabytes over the network and fill the very
            // disk it was asked to free. ReparseTags already warned that any future code
            // opening files must honour this; this is that code.
            const uint CloudAttributes = (uint)(FileAttributes_.Offline
                | FileAttributes_.RecallOnDataAccess
                | FileAttributes_.RecallOnOpen);

            if ((info.FileAttributes & CloudAttributes) != 0
                || (info.IsReparsePoint && ReparseTags.IsCloudPlaceholder(info.ReparseTag)))
            {
                CloudSkipped++;
                return;
            }

            // Logical size, not allocation: see the class remarks.
            if (info.EndOfFile < MinimumSize)
            {
                return;
            }

            Files.Add(new CandidateFile(DirIndex, name.ToString(), info.EndOfFile, info.FileId));
        }
    }
}
