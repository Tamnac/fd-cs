using System.Runtime.InteropServices;

namespace Fq;

readonly record struct WalkStats(long Results, long Directories);

/// <summary>
/// Parallel directory walk on dedicated threads. Each worker owns a deque of directories, and
/// idle workers steal from the others. A worker that finds nothing to steal sleeps on a
/// semaphore until someone publishes new work or the last pending directory finishes.
/// Results are formatted straight from the directory record into the output buffer.
/// </summary>
sealed unsafe class Walker
{
    readonly Options options;
    readonly Filter filter;
    readonly OutputSink sink = new();
    readonly string root;
    readonly int pathOffset;
    readonly Worker[] workers;
    readonly SemaphoreSlim wake = new(0);
    readonly int lastBatchHeadroom;
    int pending;
    int idle;
    volatile bool stopped;
    long totalResults;

    public Walker(Options options)
    {
        this.options = options;
        filter = new Filter(options);
        root = Path.GetFullPath(options.Root);
        pathOffset = options.Root == "." ? Path.TrimEndingDirectorySeparator(root).Length + 1 : 0;
        lastBatchHeadroom = options.FollowSymlinks ? 0 : ProbeLastBatchHeadroom();
        workers = new Worker[options.Threads];
        for (int i = 0; i < workers.Length; i++) workers[i] = new Worker(this, i);
    }

    public WalkStats Run()
    {
        pending = 1;
        workers[0].Deque.Push(new WorkItem(root, 0));

        var threads = workers.Select(w => new Thread(w.Run)).ToArray();
        foreach (Thread t in threads) t.Start();
        foreach (Thread t in threads) t.Join();

        return new WalkStats(workers.Sum(w => w.Results), workers.Sum(w => w.Directories));
    }

    /// <summary>A followed link may lead to another volume, so the shortcut is only used when the walk stays on the root's volume.</summary>
    int ProbeLastBatchHeadroom()
    {
        nint handle = NtDirectory.Open(root);
        if (handle == 0) return 0;
        int headroom = NtDirectory.LastBatchHeadroom(handle);
        NtDirectory.NtClose(handle);
        return headroom;
    }

    sealed class Worker(Walker walker, int id)
    {
        readonly LineBuffer lines = new(walker.sink);
        readonly List<WorkItem> children = [];
        public WorkDeque Deque { get; } = new();
        public long Results { get; private set; }
        public long Directories { get; private set; }

        public void Run()
        {
            byte* buffer = (byte*)NativeMemory.AlignedAlloc(NtDirectory.BufferSize, 8);
            while (TryAcquire(out WorkItem item))
            {
                if (!walker.stopped) Scan(item, buffer);
                if (Interlocked.Decrement(ref walker.pending) == 0) walker.wake.Release(walker.workers.Length);
            }
            NativeMemory.AlignedFree(buffer);
            lines.Flush();
        }

        bool TryAcquire(out WorkItem item)
        {
            while (true)
            {
                if (TryTake(out item)) return true;
                if (Volatile.Read(ref walker.pending) == 0) return false;

                // Announce idleness, then look once more: a producer that missed the
                // announcement must have published its work before we look.
                Interlocked.Increment(ref walker.idle);
                if (TryTake(out item)) { Interlocked.Decrement(ref walker.idle); return true; }
                if (Volatile.Read(ref walker.pending) == 0) { Interlocked.Decrement(ref walker.idle); return false; }
                walker.wake.Wait();
                Interlocked.Decrement(ref walker.idle);
            }
        }

        bool TryTake(out WorkItem item)
        {
            if (Deque.TryPopNewest(out item)) return true;
            var all = walker.workers;
            for (int i = 1; i < all.Length; i++)
            {
                if (all[(id + i) % all.Length].Deque.TryStealOldest(out item)) return true;
            }
            return false;
        }

        void Scan(WorkItem item, byte* buffer)
        {
            nint handle = NtDirectory.Open(item.Directory);
            if (handle == 0) return;
            Directories++;

            try
            {
                bool restart = true;
                int size;
                while (!walker.stopped && (size = NtDirectory.ReadBatch(handle, buffer, restart)) >= 0)
                {
                    restart = false;
                    if (!ProcessBatch(item, buffer)) break;
                    if (walker.lastBatchHeadroom > 0 && NtDirectory.BufferSize - size >= walker.lastBatchHeadroom) break;
                }
            }
            finally
            {
                NtDirectory.NtClose(handle);
            }

            Publish();
        }

        bool ProcessBatch(WorkItem item, byte* buffer)
        {
            var o = walker.options;
            var filter = walker.filter;
            var skippedAttributes = o.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System;

            for (byte* p = buffer; ; p += new DirectoryRecord(p).NextEntryOffset)
            {
                var entry = new DirectoryRecord(p);
                ReadOnlySpan<char> name = entry.Name;
                bool special = name is "." or "..";

                if (!special && (entry.Attributes & skippedAttributes) == 0)
                {
                    if (!entry.IsDirectory)
                    {
                        if (o.IncludeFiles && filter.MatchesFile(name, entry.Length, entry.LastWriteTime) && !Report(item.Directory, name))
                            return false;
                    }
                    else if (filter.ShouldTraverse(name, entry.Attributes))
                    {
                        if (item.Depth < o.MaxDepth)
                            children.Add(new WorkItem(Path.Join(item.Directory, name), item.Depth + 1));
                        if (o.IncludeDirectories && filter.MatchesDirectory(name, entry.LastWriteTime) && !Report(item.Directory, name))
                            return false;
                    }
                }

                if (entry.NextEntryOffset == 0) return true;
            }
        }

        void Publish()
        {
            if (children.Count == 0) return;
            Interlocked.Add(ref walker.pending, children.Count);
            foreach (WorkItem child in children) Deque.Push(child);
            int sleepers = Volatile.Read(ref walker.idle);
            if (sleepers > 0) walker.wake.Release(Math.Min(sleepers, children.Count));
            children.Clear();
        }

        bool Report(string directory, ReadOnlySpan<char> name)
        {
            int limit = walker.options.MaxResults;
            if (limit > 0)
            {
                long n = Interlocked.Increment(ref walker.totalResults);
                if (n > limit) { walker.stopped = true; return false; }
                if (n == limit) walker.stopped = true;
            }
            Results++;
            if (!walker.options.Quiet) lines.Add(directory, name, walker.pathOffset);
            return true;
        }
    }
}
