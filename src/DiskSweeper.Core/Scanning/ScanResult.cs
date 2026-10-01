namespace DiskSweeper.Core.Scanning;

public enum ScanMethod
{
    /// <summary>Listed every folder through normal Windows file APIs.</summary>
    FolderWalk,

    /// <summary>Read the NTFS Master File Table directly (needs administrator rights, much faster).</summary>
    MasterFileTable,

    /// <summary>Loaded from a saved scan file.</summary>
    Saved,
}

public sealed class ScanResult
{
    internal ScanResult(
        FileNode root,
        DateTime scannedAtUtc,
        TimeSpan duration,
        ScanMethod method,
        IReadOnlyList<string> unreadableFolders,
        long linkCount,
        long cloudOnlyFileCount,
        long cloudOnlyBytes)
    {
        Root = root;
        ScannedAtUtc = scannedAtUtc;
        Duration = duration;
        Method = method;
        UnreadableFolders = unreadableFolders;
        LinkCount = linkCount;
        CloudOnlyFileCount = cloudOnlyFileCount;
        CloudOnlyBytes = cloudOnlyBytes;
    }

    public FileNode Root { get; }

    public DateTime ScannedAtUtc { get; }

    public TimeSpan Duration { get; }

    public ScanMethod Method { get; }

    /// <summary>Folders that could not be listed, usually because of permissions. Their sizes are missing from the totals.</summary>
    public IReadOnlyList<string> UnreadableFolders { get; }

    /// <summary>Junctions and symbolic links that were seen but not followed.</summary>
    public long LinkCount { get; }

    public long CloudOnlyFileCount { get; }

    /// <summary>Size of online-only cloud files. Not included in any folder total because they use no local space.</summary>
    public long CloudOnlyBytes { get; }

    /// <summary>Every file and folder, parents before children.</summary>
    public IEnumerable<FileNode> EnumerateNodes() => EnumerateNodes(Root);

    public static IEnumerable<FileNode> EnumerateNodes(FileNode start)
    {
        var stack = new Stack<FileNode>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    /// <summary>The biggest files in the scan, largest first, optionally limited to those that match <paramref name="filter"/>.</summary>
    public IReadOnlyList<FileNode> GetLargestFiles(int count, Func<FileNode, bool>? filter = null) =>
        GetLargestFiles(Root, count, filter);

    public static IReadOnlyList<FileNode> GetLargestFiles(FileNode start, int count, Func<FileNode, bool>? filter = null)
    {
        if (count <= 0) return [];

        // Min-heap of the best `count` files so far: the smallest of them sits on top, ready to be replaced.
        var heap = new PriorityQueue<FileNode, long>(count + 1);
        foreach (var node in EnumerateNodes(start))
        {
            if (node.IsDirectory || (filter is not null && !filter(node))) continue;
            if (heap.Count < count)
            {
                heap.Enqueue(node, node.Size);
            }
            else if (heap.TryPeek(out _, out long smallest) && node.Size > smallest)
            {
                heap.EnqueueDequeue(node, node.Size);
            }
        }

        var largest = new FileNode[heap.Count];
        for (int i = largest.Length - 1; i >= 0; i--) largest[i] = heap.Dequeue();
        return largest;
    }

    /// <summary>Finds the node for a full path inside this scan, or null if the path isn't part of it.</summary>
    public FileNode? FindNode(string path)
    {
        string full;
        try
        {
            full = LongPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!LongPath.IsSameOrInside(full, Root.Name)) return null;

        var node = Root;
        string rest = full[Root.Name.Length..].Trim(Path.DirectorySeparatorChar);
        if (rest.Length == 0) return node;

        foreach (string part in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            FileNode? next = null;
            foreach (var child in node.Children)
            {
                if (string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }
            if (next is null) return null;
            node = next;
        }
        return node;
    }
}
