using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Core.Analysis;

public enum ChangeKind
{
    Grew,
    Shrank,
    New,
    Gone,
}

public sealed record SizeChange(string Path, bool IsDirectory, long OldSize, long NewSize, ChangeKind Kind)
{
    public long Delta => NewSize - OldSize;
}

/// <summary>
/// Compares two scans of the same folder and points at the places that actually changed. If C:\Users grew
/// by 20 GB because one game folder deep inside grew, the result names that game folder, not every folder
/// on the way down to it.
/// </summary>
public static class ScanComparer
{
    /// <summary>Share of a folder's change that its listed subfolders must explain for the folder itself to stay unlisted.</summary>
    private const double Explained = 0.8;

    public static IReadOnlyList<SizeChange> Compare(FileNode oldRoot, FileNode newRoot, long minimumChange = 100L << 20, int maxResults = 200)
    {
        var changes = new List<SizeChange>();
        Visit(oldRoot, newRoot, newRoot.FullPath, minimumChange, changes);
        return changes.OrderByDescending(c => Math.Abs(c.Delta)).Take(maxResults).ToList();
    }

    private static void Visit(FileNode oldNode, FileNode newNode, string path, long minimumChange, List<SizeChange> changes)
    {
        long delta = newNode.Size - oldNode.Size;
        if (Math.Abs(delta) < minimumChange) return;

        long explained = 0;
        if (oldNode.IsDirectory && newNode.IsDirectory)
        {
            var oldChildren = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in oldNode.Children) oldChildren.TryAdd(child.Name, child);

            foreach (var child in newNode.Children)
            {
                string childPath = Path.Join(path, child.Name);
                if (oldChildren.Remove(child.Name, out var oldChild))
                {
                    long childDelta = child.Size - oldChild.Size;
                    if (Math.Abs(childDelta) < minimumChange) continue;
                    Visit(oldChild, child, childPath, minimumChange, changes);
                    explained += childDelta;
                }
                else if (child.Size >= minimumChange)
                {
                    changes.Add(new SizeChange(childPath, child.IsDirectory, 0, child.Size, ChangeKind.New));
                    explained += child.Size;
                }
            }

            foreach (var gone in oldChildren.Values.Where(c => c.Size >= minimumChange))
            {
                changes.Add(new SizeChange(Path.Join(path, gone.Name), gone.IsDirectory, gone.Size, 0, ChangeKind.Gone));
                explained -= gone.Size;
            }
        }

        // List this folder too when its change is spread over many small items rather than a few big ones.
        bool sameDirection = Math.Sign(explained) == Math.Sign(delta);
        if (!sameDirection || Math.Abs(explained) < Math.Abs(delta) * Explained)
            changes.Add(new SizeChange(path, newNode.IsDirectory, oldNode.Size, newNode.Size, delta > 0 ? ChangeKind.Grew : ChangeKind.Shrank));
    }
}
