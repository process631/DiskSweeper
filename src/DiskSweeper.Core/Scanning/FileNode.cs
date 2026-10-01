using System.Text;

namespace DiskSweeper.Core.Scanning;

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    Directory = 1 << 0,

    /// <summary>A junction or symbolic link. Its target was not scanned, so it counts as 0 bytes.</summary>
    Link = 1 << 1,

    /// <summary>The folder could not be listed (usually "access denied"), so its size is incomplete.</summary>
    Unreadable = 1 << 2,

    /// <summary>A OneDrive-style online-only placeholder. It takes no space on this PC.</summary>
    CloudOnly = 1 << 3,
}

/// <summary>One file or folder in a scanned tree.</summary>
public sealed class FileNode
{
    private static readonly FileNode[] NoChildren = [];

    internal FileNode(string name, FileNode? parent, NodeFlags flags, long size, DateTime lastWriteTimeUtc, FileAttributes attributes)
    {
        Name = name;
        Parent = parent;
        Flags = flags;
        Size = size;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Attributes = attributes;
    }

    /// <summary>File or folder name. For the scan root this is the full path.</summary>
    public string Name { get; }

    public FileNode? Parent { get; internal set; }

    public NodeFlags Flags { get; internal set; }

    /// <summary>Bytes used on this PC. For folders, the total of everything inside.</summary>
    public long Size { get; internal set; }

    /// <summary>Files inside this folder at any depth. 0 for files.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Folders inside this folder at any depth. 0 for files.</summary>
    public long FolderCount { get; internal set; }

    /// <summary>For files, when the file was last changed. For folders, the newest change anywhere inside.</summary>
    public DateTime LastWriteTimeUtc { get; internal set; }

    public FileAttributes Attributes { get; }

    /// <summary>Contents of a folder, largest first. Empty for files.</summary>
    public IReadOnlyList<FileNode> Children => (IReadOnlyList<FileNode>?)ChildList ?? NoChildren;

    internal List<FileNode>? ChildList { get; set; }

    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;

    public bool IsLink => (Flags & NodeFlags.Link) != 0;

    public bool IsUnreadable => (Flags & NodeFlags.Unreadable) != 0;

    public bool IsCloudOnly => (Flags & NodeFlags.CloudOnly) != 0;

    /// <summary>Extension in lower case including the dot (".pak"), or "" when there is none. Always "" for folders.</summary>
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();

    public int Depth
    {
        get
        {
            int depth = 0;
            for (var node = Parent; node is not null; node = node.Parent) depth++;
            return depth;
        }
    }

    /// <summary>Full path, built from the parent chain on demand so millions of nodes don't each store one.</summary>
    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;

            var chain = new Stack<string>();
            var node = this;
            for (; node.Parent is not null; node = node.Parent) chain.Push(node.Name);

            var path = new StringBuilder(node.Name);
            while (chain.Count > 0)
            {
                if (path[^1] != Path.DirectorySeparatorChar) path.Append(Path.DirectorySeparatorChar);
                path.Append(chain.Pop());
            }
            return path.ToString();
        }
    }

    /// <summary>True when <paramref name="other"/> is this node or anywhere inside it.</summary>
    public bool Contains(FileNode other)
    {
        for (var node = other; node is not null; node = node.Parent)
        {
            if (ReferenceEquals(node, this)) return true;
        }
        return false;
    }

    public override string ToString() => $"{FullPath} ({ByteSize.Format(Size)})";
}
