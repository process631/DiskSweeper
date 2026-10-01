namespace DiskSweeper.Core.Scanning;

internal static class TreeTotals
{
    internal static readonly Comparison<FileNode> LargestFirst = (a, b) =>
    {
        int bySize = b.Size.CompareTo(a.Size);
        return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    };

    /// <summary>
    /// Rolls sizes, counts and newest-change times up from subfolders into their parents, then sorts every
    /// folder's contents largest first. Before this runs, each folder's Size holds only the files directly in it.
    /// </summary>
    public static void Compute(FileNode root)
    {
        // Some files carry impossible timestamps (years in the future); they mustn't make a folder look "just changed".
        var latestPlausible = DateTime.UtcNow.AddDays(1);

        // Parents come before their subfolders in this list, so walking it backwards
        // handles every subfolder before the folder that contains it.
        var folders = new List<FileNode>();
        var stack = new Stack<FileNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            folders.Add(folder);
            foreach (var child in folder.Children)
            {
                if (child.ChildList is not null) stack.Push(child);
            }
        }

        for (int i = folders.Count - 1; i >= 0; i--)
        {
            var folder = folders[i];
            var children = folder.ChildList;
            if (children is null) continue;

            foreach (var child in children)
            {
                if (child.LastWriteTimeUtc > folder.LastWriteTimeUtc && child.LastWriteTimeUtc <= latestPlausible)
                    folder.LastWriteTimeUtc = child.LastWriteTimeUtc;
                if (!child.IsDirectory) continue;

                folder.Size += child.Size;
                folder.FileCount += child.FileCount;
                folder.FolderCount += child.FolderCount + 1;
            }
            children.Sort(LargestFirst);
            children.TrimExcess();
        }
    }
}
