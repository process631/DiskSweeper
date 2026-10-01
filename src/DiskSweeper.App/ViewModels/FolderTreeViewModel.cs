using System.Windows;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App.ViewModels;

public enum TreeSort
{
    Size,
    Name,
    Files,
    Modified,
}

/// <summary>One line of the folder tree: a file or folder with its size, share of its parent, and tick box.</summary>
public sealed class TreeRowViewModel(FileNode node, int depth, FolderTreeViewModel owner) : ObservableObject
{
    private bool _isExpanded;

    public FileNode Node { get; } = node;

    public int Depth { get; } = depth;

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public string Name => Node.Name;

    public string Icon => Node.IsLink ? Icons.Link : Node.IsDirectory ? Icons.Folder : Icons.File;

    public string Tag => Node.IsLink ? "link, not followed" : Node.IsUnreadable ? "no access" : Node.IsCloudOnly ? "online-only" : "";

    public bool HasTag => Tag.Length > 0;

    public bool CanExpand => Node.IsDirectory && Node.Children.Count > 0;

    public string SizeText => ByteSize.Format(Node.Size);

    public double Share => Node.Parent is { Size: > 0 } parent ? (double)Node.Size / parent.Size : 1;

    public string ShareText => $"{Share:P1}";

    public double BarWidth => Math.Max(0, Share * 70);

    public string FilesText => Node.IsDirectory ? Node.FileCount.ToString("N0") : "";

    public string ModifiedText => Node.LastWriteTimeUtc > DateTime.MinValue.AddYears(1) ? Node.LastWriteTimeUtc.ToLocalTime().ToString("d MMM yyyy") : "";

    /// <summary>Protected things (drives, Windows, your profile) can't be ticked, so they can't be deleted by accident.</summary>
    public bool CanSelect => Depth > 0 && ProtectedPaths.WhyProtected(Node.FullPath) is null;

    public string? ProtectedReason => Depth == 0 ? "This is the scanned folder itself." : ProtectedPaths.WhyProtected(Node.FullPath);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (value == _isExpanded || !CanExpand) return;
            _isExpanded = value;
            Raise();
            owner.OnExpandedChanged(this);
        }
    }

    internal void SetExpandedSilently(bool value)
    {
        _isExpanded = value;
        Raise(nameof(IsExpanded));
    }

    public bool IsChecked
    {
        get => owner.Selection.Contains(Node.FullPath);
        set => owner.Selection.Set(SelectedItem.From(Node), value);
    }

    public void RefreshChecked() => Raise(nameof(IsChecked));

    public void RefreshAll() => RaiseAll();
}

/// <summary>
/// The Folders tab: a tree shown as a flat, virtualized list (so folders with 20,000 items stay fast).
/// Expanding a row inserts its children right below it; sorting re-orders siblings at every level.
/// </summary>
public sealed class FolderTreeViewModel : ObservableObject
{
    private readonly HashSet<FileNode> _expanded = [];
    private FileNode? _root;
    private TreeSort _sort = TreeSort.Size;
    private bool _descending = true;
    private TreeRowViewModel? _selectedRow;

    public FolderTreeViewModel(SelectionViewModel selection)
    {
        Selection = selection;
        selection.Changed += (_, _) =>
        {
            foreach (var row in Rows) row.RefreshChecked();
        };
    }

    public SelectionViewModel Selection { get; }

    public BulkObservableCollection<TreeRowViewModel> Rows { get; } = [];

    public TreeRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => Set(ref _selectedRow, value);
    }

    public TreeSort Sort => _sort;

    public bool Descending => _descending;

    public string SortDescription => $"Sorted by {_sort.ToString().ToLowerInvariant()}, {(_descending ? "largest/newest first" : "smallest/oldest first")}";

    public void Load(FileNode root)
    {
        _root = root;
        _expanded.Clear();
        _expanded.Add(root);
        // Open the path to the biggest item so the first view already says something useful.
        var node = root;
        for (int i = 0; i < 2 && node.Children.FirstOrDefault(c => c.IsDirectory) is FileNode biggest; i++)
        {
            _expanded.Add(biggest);
            node = biggest;
        }
        Rebuild();
    }

    public void SortBy(TreeSort sort)
    {
        _descending = sort == _sort ? !_descending : sort is TreeSort.Size or TreeSort.Files or TreeSort.Modified;
        _sort = sort;
        Raise(nameof(SortDescription));
        Rebuild();
    }

    /// <summary>Rebuilds the visible rows after the tree changed (for example after deleting), keeping what was expanded.</summary>
    public void Rebuild()
    {
        var keep = SelectedRow?.Node;
        var rows = new List<TreeRowViewModel>();
        if (_root is not null) AddRows(_root, 0, rows);
        Rows.ReplaceAll(rows);
        SelectedRow = keep is null ? null : Rows.FirstOrDefault(r => r.Node == keep);
    }

    /// <summary>Expands every folder above <paramref name="node"/> and selects it.</summary>
    public void Reveal(FileNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) _expanded.Add(parent);
        Rebuild();
        SelectedRow = Rows.FirstOrDefault(r => r.Node == node);
    }

    internal void OnExpandedChanged(TreeRowViewModel row)
    {
        int index = Rows.IndexOf(row);
        if (index < 0) return;

        if (row.IsExpanded)
        {
            _expanded.Add(row.Node);
            var children = new List<TreeRowViewModel>();
            foreach (var child in Sorted(row.Node.Children)) AddRows(child, row.Depth + 1, children);
            Rows.InsertRange(index + 1, children);
        }
        else
        {
            _expanded.Remove(row.Node);
            int end = index + 1;
            while (end < Rows.Count && Rows[end].Depth > row.Depth) end++;
            Rows.RemoveRange(index + 1, end - index - 1);
        }
        SelectedRow = row;
    }

    private void AddRows(FileNode node, int depth, List<TreeRowViewModel> rows)
    {
        var row = new TreeRowViewModel(node, depth, this);
        rows.Add(row);
        if (!_expanded.Contains(node) || !row.CanExpand) return;

        row.SetExpandedSilently(true);
        foreach (var child in Sorted(node.Children)) AddRows(child, depth + 1, rows);
    }

    private IEnumerable<FileNode> Sorted(IReadOnlyList<FileNode> children)
    {
        // Children are stored largest first already, so the default order needs no work.
        if (_sort == TreeSort.Size && _descending) return children;

        IOrderedEnumerable<FileNode> ordered = _sort switch
        {
            TreeSort.Name => children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            TreeSort.Files => children.OrderBy(c => c.FileCount),
            TreeSort.Modified => children.OrderBy(c => c.LastWriteTimeUtc),
            _ => children.OrderBy(c => c.Size),
        };
        return _descending ? ordered.Reverse() : ordered;
    }
}
