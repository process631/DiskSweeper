namespace DiskSweeper.Core.Scanning;

/// <summary>Keeps a scanned tree accurate after deleting, without rescanning the whole drive.</summary>
public static class TreeEditor
{
    /// <summary>Takes a deleted file or folder out of the tree and subtracts it from every folder above.</summary>
    public static void Remove(FileNode node)
    {
        var parent = node.Parent;
        if (parent?.ChildList is null || !parent.ChildList.Remove(node)) return;

        long files = node.IsDirectory ? node.FileCount : 1;
        long folders = node.IsDirectory ? node.FolderCount + 1 : 0;
        Adjust(parent, -node.Size, -files, -folders);
    }

    /// <summary>
    /// Re-measures one file or folder (for example after a delete only partly succeeded) and updates the
    /// tree. If it no longer exists it is removed.
    /// </summary>
    public static async Task RefreshAsync(FileNode node, CancellationToken cancellationToken = default)
    {
        string path = node.FullPath;
        var attributes = FileSystemWalk.GetAttributes(path);
        if (attributes is null)
        {
            Remove(node);
            return;
        }

        if (!node.IsDirectory)
        {
            long length = new FileInfo(LongPath.ToExtended(path)).Length;
            long change = length - node.Size;
            node.Size = length;
            if (node.Parent is not null) Adjust(node.Parent, change, 0, 0);
            return;
        }

        var fresh = await new DiskScanner().ScanAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        long sizeChange = fresh.Root.Size - node.Size;
        long fileChange = fresh.Root.FileCount - node.FileCount;
        long folderChange = fresh.Root.FolderCount - node.FolderCount;

        node.ChildList = fresh.Root.ChildList;
        foreach (var child in node.Children) child.Parent = node;
        node.Size = fresh.Root.Size;
        node.FileCount = fresh.Root.FileCount;
        node.FolderCount = fresh.Root.FolderCount;
        node.Flags = (node.Flags & ~NodeFlags.Unreadable) | (fresh.Root.Flags & NodeFlags.Unreadable);
        if (node.Parent is not null) Adjust(node.Parent, sizeChange, fileChange, folderChange);
    }

    private static void Adjust(FileNode start, long size, long files, long folders)
    {
        for (var folder = start; folder is not null; folder = folder.Parent)
        {
            folder.Size += size;
            folder.FileCount += files;
            folder.FolderCount += folders;
            folder.ChildList?.Sort(TreeTotals.LargestFirst);
        }
    }
}
