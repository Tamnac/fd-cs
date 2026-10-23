using System.Runtime.InteropServices;

namespace Fq;

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
    readonly IgnoreRules ignoreRules;
    readonly Gitignore? overrides;
    readonly Func<ResultWriter> createWriter;
    readonly List<RootInfo> roots;
    readonly Worker[] workers;
    readonly SemaphoreSlim wake = new(0);
    readonly int lastBatchHeadroom;
    readonly int maxDepth, minDepth, maxResults;
    readonly bool follow, prune;
    int pending;
    int idle;
    volatile bool stopped;
    long totalResults;

    public Walker(Options options, List<RootInfo> roots, Func<ResultWriter> createWriter)
    {
        this.options = options;
        this.roots = roots;
        this.createWriter = createWriter;
        filter = new Filter(options);
        ignoreRules = new IgnoreRules(options);
        overrides = options.Excludes.Count > 0 ? Gitignore.Parse(options.Excludes) : null;
        maxDepth = options.MaxDepth;
        minDepth = options.MinDepth;
        maxResults = options.MaxResults;
        follow = options.Follow;
        prune = options.Prune;
        lastBatchHeadroom = follow ? 0 : ProbeLastBatchHeadroom(roots[0].FullPath);
        workers = new Worker[options.Threads];
        for (int i = 0; i < workers.Length; i++) workers[i] = new Worker(this, i);
    }

    /// <summary>Walks all roots and returns the number of results.</summary>
    public long Run()
    {
        foreach (RootInfo root in roots)
        {
            if (maxDepth == 0 || HasIgnoreContainEntry(root.FullPath)) continue;

            root.ParentIgnore = ignoreRules.LoadParents(root.FullPath);
            if (options.OneFileSystem) root.VolumeSerial = ReadVolumeSerial(root.FullPath);
            workers[pending++ % workers.Length].Deque.Push(new WorkItem(root.FullPath, 0, root, root.ParentIgnore, null));
        }

        var threads = workers.Select(w => new Thread(w.Run)).ToArray();
        foreach (Thread t in threads) t.Start();
        foreach (Thread t in threads) t.Join();

        return workers.Sum(w => w.Results);
    }

    bool HasIgnoreContainEntry(string directory)
    {
        foreach (string entry in options.IgnoreContain)
        {
            if (Path.Exists(Path.Join(directory, entry))) return true;
        }
        return false;
    }

    static uint ReadVolumeSerial(string directory)
    {
        nint handle = NtDirectory.Open(directory);
        if (handle == 0) return 0;
        uint serial = NtDirectory.GetVolumeSerial(handle);
        NtDirectory.NtClose(handle);
        return serial;
    }

    /// <summary>A followed link may lead to another volume, so the shortcut is only used when the walk stays on the root's volume.</summary>
    static int ProbeLastBatchHeadroom(string directory)
    {
        nint handle = NtDirectory.Open(directory);
        if (handle == 0) return 0;
        int headroom = NtDirectory.LastBatchHeadroom(handle);
        NtDirectory.NtClose(handle);
        return headroom;
    }

    void ReportUnreadable(string directory)
    {
        if (options.ShowErrors) Console.Error.WriteLine($"[fq error]: Cannot read directory '{directory}'");
    }

    sealed class Worker(Walker walker, int id)
    {
        readonly ResultWriter writer = walker.createWriter();
        readonly List<WorkItem> children = [];
        char[] pathBuffer = new char[512];
        byte* listing;
        int listingCapacity;
        public WorkDeque Deque { get; } = new();
        public long Results { get; private set; }

        public void Run()
        {
            byte* buffer = (byte*)NativeMemory.AlignedAlloc(NtDirectory.BufferSize, 8);
            while (TryAcquire(out WorkItem item))
            {
                if (!walker.stopped) Scan(item, buffer);
                if (Interlocked.Decrement(ref walker.pending) == 0) walker.wake.Release(walker.workers.Length);
            }
            NativeMemory.AlignedFree(buffer);
            if (listing != null) NativeMemory.AlignedFree(listing);
            writer.Flush();
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
            if (handle == 0)
            {
                walker.ReportUnreadable(item.Directory);
                return;
            }

            try
            {
                AncestorNode? ancestry = item.Ancestry;
                if (walker.follow && NtDirectory.TryGetFileId(handle, out FileId id))
                {
                    for (AncestorNode? a = ancestry; a is not null; a = a.Parent)
                    {
                        if (a.Id == id) return;
                    }
                    ancestry = new AncestorNode(id, ancestry);
                }
                if (walker.options.OneFileSystem && NtDirectory.GetVolumeSerial(handle) != item.Root.VolumeSerial) return;

                if (walker.ignoreRules.Enabled) ScanWithIgnoreFiles(item, handle, ancestry);
                else ScanStreaming(item, handle, buffer, ancestry);
            }
            finally
            {
                NtDirectory.NtClose(handle);
            }

            Publish();
        }

        void ScanStreaming(WorkItem item, nint handle, byte* buffer, AncestorNode? ancestry)
        {
            bool restart = true;
            int size;
            while (!walker.stopped && (size = NtDirectory.ReadBatch(handle, buffer, restart)) >= 0)
            {
                restart = false;
                if (!ProcessEntries(item, buffer, null, ancestry)) break;
                if (walker.lastBatchHeadroom > 0 && NtDirectory.BufferSize - size >= walker.lastBatchHeadroom) break;
            }
        }

        /// <summary>
        /// A directory's ignore files apply to all of its entries, wherever they are listed, so the whole listing is read
        /// first. Batches are stored back to back and chained into one list of records.
        /// </summary>
        void ScanWithIgnoreFiles(WorkItem item, nint handle, AncestorNode? ancestry)
        {
            int used = 0, lastRecord = -1;
            bool restart = true;
            var found = IgnoreFileKinds.None;

            while (!walker.stopped)
            {
                EnsureListingCapacity(used + NtDirectory.BufferSize);
                int size = NtDirectory.ReadBatch(handle, listing + used, restart);
                if (size < 0) break;
                restart = false;

                if (lastRecord >= 0) *(uint*)(listing + lastRecord) = (uint)(used - lastRecord);
                for (int offset = used; ; )
                {
                    var record = new DirectoryRecord(listing + offset);
                    if (record.Name.Length >= 4 && record.Name[0] == '.') found |= IgnoreRules.Classify(record.Name, record.Attributes);
                    lastRecord = offset;
                    if (record.NextEntryOffset == 0) break;
                    offset += (int)record.NextEntryOffset;
                }

                used = (used + size + 7) & ~7;
                if (walker.lastBatchHeadroom > 0 && NtDirectory.BufferSize - size >= walker.lastBatchHeadroom) break;
            }

            if (used == 0) return;
            IgnoreLevel? level = found == IgnoreFileKinds.None ? item.Ignore : walker.ignoreRules.Load(item.Ignore, item.Directory, found);
            ProcessEntries(item, listing, level, ancestry);

            if (listingCapacity > 1 << 22)
            {
                NativeMemory.AlignedFree(listing);
                listing = null;
                listingCapacity = 0;
            }
        }

        void EnsureListingCapacity(int capacity)
        {
            if (listingCapacity >= capacity) return;
            int newCapacity = Math.Max(capacity, Math.Max(listingCapacity * 2, 1 << 15));
            listing = (byte*)(listing == null ? NativeMemory.AlignedAlloc((nuint)newCapacity, 8) : NativeMemory.AlignedRealloc(listing, (nuint)newCapacity, 8));
            listingCapacity = newCapacity;
        }

        /// <summary>Walks a chain of directory records: applies exclusions, queues subdirectories and reports matches. Returns false to stop the walk.</summary>
        bool ProcessEntries(WorkItem item, byte* first, IgnoreLevel? level, AncestorNode? ancestry)
        {
            var filter = walker.filter;
            string directory = item.Directory;
            RootInfo root = item.Root;
            int depth = item.Depth + 1;
            bool canDescend = depth < walker.maxDepth;
            bool inRange = depth >= walker.minDepth;
            ReadOnlySpan<char> rootRelative = directory.Length > root.Skip ? directory.AsSpan(root.Skip) : [];

            for (byte* p = first; ; p += new DirectoryRecord(p).NextEntryOffset)
            {
                var entry = new DirectoryRecord(p);
                ReadOnlySpan<char> name = entry.Name;

                if (name is not ("." or ".."))
                {
                    EntryKinds kind = entry.Kind;
                    if (kind == EntryKinds.Link && walker.follow) kind = entry.IsDirectory ? EntryKinds.Directory : EntryKinds.File;
                    bool isDirectory = kind == EntryKinds.Directory;

                    if (!IsExcluded(directory, level, rootRelative, name, entry.Attributes, isDirectory)
                        && !(isDirectory && walker.options.IgnoreContain.Count > 0 && walker.HasIgnoreContainEntry(Path.Join(directory, name))))
                    {
                        bool report = inRange && filter.Matches(kind, name, entry.Length, entry.LastWriteTime)
                            && (!filter.NeedsPath || filter.MatchesPath(kind, FullPath(directory, name)));

                        if (isDirectory && canDescend && !(report && walker.prune))
                            children.Add(new WorkItem(Path.Join(directory, name), depth, root, level, ancestry));
                        if (report && !Report(root, directory, name, kind)) return false;
                    }
                }

                if (entry.NextEntryOffset == 0) return true;
            }
        }

        /// <summary>--exclude globs first, then ignore files; hidden entries go unless an ignore file whitelists them.</summary>
        bool IsExcluded(string directory, IgnoreLevel? level, ReadOnlySpan<char> rootRelative, ReadOnlySpan<char> name, FileAttributes attributes, bool isDirectory)
        {
            var filter = walker.filter;
            if (isDirectory && filter.IsSkipped(name)) return true;

            bool hidden = filter.IsHidden(name, attributes);
            var rules = walker.ignoreRules;
            if (hidden && !(rules.Enabled && rules.MayWhitelist(level))) return true;

            if (walker.overrides is { } overrides && overrides.Match(rootRelative, name, isDirectory) == IgnoreMatch.Ignore) return true;
            if (rules.Enabled)
            {
                switch (rules.Match(level, directory, rootRelative, name, isDirectory))
                {
                    case IgnoreMatch.Ignore: return true;
                    case IgnoreMatch.Whitelist: return false;
                }
            }
            return hidden;
        }

        ReadOnlySpan<char> FullPath(string directory, ReadOnlySpan<char> name)
        {
            bool separator = !directory.EndsWith('\\');
            int length = directory.Length + (separator ? 1 : 0) + name.Length;
            if (pathBuffer.Length < length) pathBuffer = new char[length * 2];
            directory.CopyTo(pathBuffer);
            if (separator) pathBuffer[directory.Length] = '\\';
            name.CopyTo(pathBuffer.AsSpan(length - name.Length));
            return pathBuffer.AsSpan(0, length);
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

        bool Report(RootInfo root, string directory, ReadOnlySpan<char> name, EntryKinds kind)
        {
            int limit = walker.maxResults;
            if (limit > 0)
            {
                long n = Interlocked.Increment(ref walker.totalResults);
                if (n > limit) { walker.stopped = true; return false; }
                if (n == limit) walker.stopped = true;
            }
            Results++;
            writer.Add(root, directory, name, kind);
            return true;
        }
    }
}
