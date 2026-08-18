using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silt.Core.Collections;
using Silt.Core.Native;

namespace Silt.Core.Scanning;

/// <summary>
/// Parallel breadth-first directory scanner.
/// </summary>
/// <remarks>
/// <para>
/// Each directory is enumerated by exactly one worker, which means a node's
/// <see cref="ScanNode.Children"/> list is only ever touched by a single thread and needs no
/// lock. The only shared state is the work queue, the pending counter, the aggregate
/// counters, and the hardlink set.
/// </para>
/// <para>
/// Errors are counted, never swallowed. A scanner that silently skips unreadable subtrees
/// produces a total that looks authoritative and is not â€” which is the specific failure this
/// product exists to correct.
/// </para>
/// </remarks>
public sealed class BfsScanner : IVolumeScanner
{
    public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();

        string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        if (rootPath.Length == 2 && rootPath[1] == ':')
        {
            rootPath += Path.DirectorySeparatorChar; // "C:" is the current dir on C:, not its root
        }

        var root = new ScanNode
        {
            Name = rootPath,
            Parent = null,
        };

        // The queue carries the path alongside the node so it never has to be stored on the
        // node itself. These strings live only while an item is queued and are collected
        // immediately afterwards, instead of being retained for the life of the result.
        var queue = new ConcurrentQueue<WorkItem>();
        queue.Enqueue(new WorkItem(root, rootPath));

        var dedup = options.DeduplicateHardLinks ? new ConcurrentFileIdSet(1 << 20) : null;

        // PendingRef lives on the shared Counters instance rather than as a local, because
        // C# forbids capturing a `ref long` in the worker lambda.
        var counters = new Counters { PendingRef = 1 };

        int workerCount = Math.Max(1, options.DegreeOfParallelism);

        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Factory.StartNew(
                () => WorkerLoop(queue, dedup, counters, options, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task.WaitAll(workers, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        RollUp(root);
        sw.Stop();

        return new ScanResult
        {
            Root = root,
            Duration = sw.Elapsed,
            TotalFiles = root.TotalFileCount,
            TotalDirectories = root.TotalDirectoryCount,
            TotalAllocatedBytes = root.TotalAllocatedBytes,
            TotalLogicalBytes = root.TotalLogicalBytes,
            AccessDeniedCount = counters.AccessDenied,
            FailedCount = counters.Failed,
            SkippedSurrogateCount = counters.SkippedSurrogates,
            HardLinkBytesDeduplicated = counters.DedupedBytes,
            HardLinkFilesDeduplicated = counters.DedupedFiles,
        };
    }

    private static unsafe void WorkerLoop(
        ConcurrentQueue<WorkItem> queue,
        ConcurrentFileIdSet? dedup,
        Counters counters,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        // One native buffer per worker, reused for every directory. Allocating per
        // directory would dominate the cost of enumerating small directories.
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(DirectoryEnumerator.BufferSize, 16);
        var subdirs = new List<PendingChild>(64);

        try
        {
            var spin = new SpinWait();

            while (true)
            {
                if (Interlocked.Read(ref counters.PendingRef) <= 0)
                {
                    return;
                }

                if (!queue.TryDequeue(out WorkItem work))
                {
                    spin.SpinOnce();
                    continue;
                }

                ScanNode node = work.Node;
                string nodePath = work.FullPath;

                spin = new SpinWait();
                cancellationToken.ThrowIfCancellationRequested();

                subdirs.Clear();
                var sink = new DirSink
                {
                    SubDirs = subdirs,
                    Dedup = dedup,
                };

                EnumerateStatus status = DirectoryEnumerator.Enumerate(
                    ToExtendedPath(nodePath), buffer, ref sink, out int win32Error);

                node.OwnAllocatedBytes = sink.AllocatedBytes;
                node.OwnLogicalBytes = sink.LogicalBytes;
                node.OwnFileCount = sink.FileCount;
                node.Win32Error = win32Error;

                switch (status)
                {
                    case EnumerateStatus.AccessDenied:
                        node.Condition |= NodeCondition.AccessDenied;
                        Interlocked.Increment(ref counters.AccessDeniedRef);
                        break;
                    case EnumerateStatus.NotFound:
                        node.Condition |= NodeCondition.Vanished;
                        break;
                    case EnumerateStatus.Failed:
                        node.Condition |= NodeCondition.Failed;
                        Interlocked.Increment(ref counters.FailedRef);
                        break;
                }

                if (sink.DedupedBytes != 0)
                {
                    Interlocked.Add(ref counters.DedupedBytesRef, sink.DedupedBytes);
                    Interlocked.Add(ref counters.DedupedFilesRef, sink.DedupedFiles);
                }

                if (subdirs.Count > 0)
                {
                    // Exact-sized array: the child count is final at this point and the
                    // list wrapper would be dead weight on every node.
                    var children = new ScanNode[subdirs.Count];
                    node.Children = children;

                    for (int i = 0; i < subdirs.Count; i++)
                    {
                        PendingChild child = subdirs[i];
                        var childNode = new ScanNode
                        {
                            Name = child.Name,
                            Parent = node,
                            Condition = child.Condition,
                        };
                        children[i] = childNode;

                        if ((child.Condition & NodeCondition.NameSurrogate) != 0)
                        {
                            // A junction or symlink. Descending would count the target's
                            // bytes a second time, and a cycle would never terminate.
                            Interlocked.Increment(ref counters.SkippedSurrogatesRef);
                            continue;
                        }

                        Interlocked.Increment(ref counters.PendingRef);
                        queue.Enqueue(new WorkItem(childNode, Path.Combine(nodePath, child.Name)));
                    }
                }

                long dirs = Interlocked.Increment(ref counters.DirectoriesRef);
                Interlocked.Add(ref counters.FilesRef, sink.FileCount);
                Interlocked.Add(ref counters.BytesRef, sink.AllocatedBytes);

                // Reporting on every directory would cost more than the scan. Every 512 is
                // frequent enough for a smooth progress bar on a multi-second scan.
                if (options.Progress is not null && (dirs & 511) == 0)
                {
                    options.Progress.Report(new ScanProgress(
                        dirs,
                        Interlocked.Read(ref counters.FilesRef),
                        Interlocked.Read(ref counters.BytesRef),
                        nodePath));
                }

                Interlocked.Decrement(ref counters.PendingRef);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>
    /// Rolls own-sizes up into subtree totals. Iterative rather than recursive: a deeply
    /// nested tree (node_modules is routinely 30+ levels) would otherwise risk a stack
    /// overflow during what is supposed to be a reporting step.
    /// </summary>
    private static void RollUp(ScanNode root)
    {
        var stack = new Stack<(ScanNode Node, bool ChildrenDone)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            (ScanNode node, bool childrenDone) = stack.Pop();

            if (!childrenDone && node.Children is { Length: > 0 })
            {
                stack.Push((node, true));
                foreach (ScanNode child in node.Children)
                {
                    stack.Push((child, false));
                }
                continue;
            }

            long allocated = node.OwnAllocatedBytes;
            long logical = node.OwnLogicalBytes;
            long files = node.OwnFileCount;
            int dirs = 0;

            if (node.Children is not null)
            {
                foreach (ScanNode child in node.Children)
                {
                    allocated += child.TotalAllocatedBytes;
                    logical += child.TotalLogicalBytes;
                    files += child.TotalFileCount;
                    dirs += child.TotalDirectoryCount + 1;
                }
            }

            node.TotalAllocatedBytes = allocated;
            node.TotalLogicalBytes = logical;
            node.TotalFileCount = files;
            node.TotalDirectoryCount = dirs;
        }
    }

    /// <summary>
    /// Prefixes with <c>\\?\</c> so paths beyond MAX_PATH work regardless of the machine's
    /// LongPathsEnabled setting. Safe here because every path is built by appending
    /// already-normalized names to a fully-qualified root, so there is nothing for the
    /// skipped normalization pass to have fixed.
    /// </summary>
    internal static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return string.Concat(@"\\?\UNC\", path.AsSpan(2));
        }
        return @"\\?\" + path;
    }

    private readonly record struct PendingChild(string Name, NodeCondition Condition);

    /// <summary>
    /// A queued directory and the path needed to open it.
    /// </summary>
    /// <remarks>
    /// The path travels with the work item rather than living on <see cref="ScanNode"/>.
    /// It is needed only while the directory is being enumerated, so keeping it here makes
    /// it short-lived garbage instead of a permanent per-node cost.
    /// </remarks>
    private readonly record struct WorkItem(ScanNode Node, string FullPath);

    /// <summary>Per-directory accumulator. A struct so the enumerator can devirtualize it.</summary>
    private struct DirSink : IEntrySink
    {
        public List<PendingChild> SubDirs;
        public ConcurrentFileIdSet? Dedup;

        public long AllocatedBytes;
        public long LogicalBytes;
        public int FileCount;
        public long DedupedBytes;
        public long DedupedFiles;

        public void OnEntry(ReadOnlySpan<char> name, in FileIdBothDirInfo info)
        {
            if (info.IsDirectory)
            {
                NodeCondition flags = NodeCondition.None;
                if (info.IsReparsePoint)
                {
                    uint tag = info.ReparseTag;
                    if (ReparseTags.IsNameSurrogate(tag))
                    {
                        flags |= NodeCondition.NameSurrogate;
                    }
                    if (ReparseTags.IsCloudPlaceholder(tag))
                    {
                        flags |= NodeCondition.CloudPlaceholder;
                    }
                }

                SubDirs.Add(new PendingChild(name.ToString(), flags));
                return;
            }

            // Hardlinked content occupies its bytes once. Counting every link would
            // over-report WinSxS by roughly 2x.
            if (Dedup is not null && !Dedup.Add(info.FileId))
            {
                DedupedBytes += info.AllocationSize;
                DedupedFiles++;
                FileCount++;
                return;
            }

            // AllocationSize is what the volume's free space actually responds to.
            // EndOfFile overstates sparse and compressed files, sometimes hugely.
            AllocatedBytes += info.AllocationSize;
            LogicalBytes += info.EndOfFile;
            FileCount++;
        }
    }

    private sealed class Counters
    {
        /// <summary>
        /// Directories discovered but not yet finished. Incremented before a child is
        /// enqueued and decremented after its parent completes, so it reaches zero exactly
        /// once â€” when every worker has run out of work rather than merely found the queue
        /// momentarily empty.
        /// </summary>
        public long PendingRef;

        public long DirectoriesRef;
        public long FilesRef;
        public long BytesRef;
        public long DedupedBytesRef;
        public long DedupedFilesRef;
        public int AccessDeniedRef;
        public int FailedRef;
        public int SkippedSurrogatesRef;

        public int AccessDenied => AccessDeniedRef;
        public int Failed => FailedRef;
        public int SkippedSurrogates => SkippedSurrogatesRef;
        public long DedupedBytes => DedupedBytesRef;
        public long DedupedFiles => DedupedFilesRef;
    }
}

