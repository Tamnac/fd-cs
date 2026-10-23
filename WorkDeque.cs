namespace Fd;

sealed record AncestorNode(FileId Id, AncestorNode? Parent);

/// <summary>A directory to list, with what its entries inherit: the search root, the ignore files above it and (when following links) the directories it is inside.</summary>
readonly record struct WorkItem(string Directory, int Depth, RootInfo Root, IgnoreLevel? Ignore, AncestorNode? Ancestry);

/// <summary>
/// Per-worker queue. The owner works newest-first (depth-first, small footprint);
/// thieves take the oldest item, which is the shallowest and so the largest subtree.
/// </summary>
sealed class WorkDeque
{
    readonly Lock gate = new();
    WorkItem[] items = new WorkItem[64];
    int head, count;

    public void Push(WorkItem item)
    {
        lock (gate)
        {
            if (count == items.Length) Grow();
            items[(head + count++) & (items.Length - 1)] = item;
        }
    }

    public bool TryPopNewest(out WorkItem item)
    {
        lock (gate)
        {
            if (count == 0) { item = default; return false; }
            int index = (head + --count) & (items.Length - 1);
            item = items[index];
            items[index] = default;
            return true;
        }
    }

    public bool TryStealOldest(out WorkItem item)
    {
        lock (gate)
        {
            if (count == 0) { item = default; return false; }
            item = items[head];
            items[head] = default;
            head = (head + 1) & (items.Length - 1);
            count--;
            return true;
        }
    }

    void Grow()
    {
        var bigger = new WorkItem[items.Length * 2];
        for (int i = 0; i < count; i++) bigger[i] = items[(head + i) & (items.Length - 1)];
        items = bigger;
        head = 0;
    }
}
