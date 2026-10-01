using System.IO;
using System.Windows.Input;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Scanning;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.App.ViewModels;

/// <summary>Something ticked for deletion: either a scanned file/folder or a Smart finds suggestion.</summary>
public sealed class SelectedItem
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Path { get; init; }

    public long Size { get; init; }

    public FileNode? Node { get; init; }

    public Finding? Finding { get; init; }

    public static SelectedItem From(FileNode node) => new()
    {
        Key = node.FullPath,
        Title = node.Name,
        Path = node.FullPath,
        Size = node.Size,
        Node = node,
    };

    public static SelectedItem From(Finding finding) => new()
    {
        Key = "finding:" + finding.Id,
        Title = finding.Title,
        Path = finding.Path,
        Size = finding.Size,
        Finding = finding,
    };
}

/// <summary>
/// The "cart" of everything ticked across all tabs. Shared by every list so a tick in one place shows up
/// everywhere, and the bottom bar always shows the real total (nested picks aren't counted twice).
/// </summary>
public sealed class SelectionViewModel : ObservableObject
{
    private readonly Dictionary<string, SelectedItem> _items = new(StringComparer.OrdinalIgnoreCase);

    public SelectionViewModel()
    {
        ClearCommand = new RelayCommand(Clear, () => _items.Count > 0);
    }

    public event EventHandler? Changed;

    public ICommand ClearCommand { get; }

    public IReadOnlyCollection<SelectedItem> Items => _items.Values;

    public int Count => _items.Count;

    public long TotalSize => WithoutNested().Sum(item => item.Size);

    public string Summary => _items.Count == 0
        ? "Tick files, folders or Smart finds to select them for deleting."
        : $"{_items.Count:N0} selected · {ByteSize.Format(TotalSize)}";

    public bool Contains(string key) => _items.ContainsKey(key);

    public void Set(SelectedItem item, bool selected)
    {
        bool changed = selected ? _items.TryAdd(item.Key, item) : _items.Remove(item.Key);
        if (changed) Notify();
    }

    public void Remove(IEnumerable<string> keys)
    {
        bool changed = false;
        foreach (string key in keys) changed |= _items.Remove(key);
        if (changed) Notify();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Notify();
    }

    /// <summary>
    /// Selected items without double counting: drops anything inside another selected item that deletes a
    /// whole file or folder, and a Smart find that points at exactly the same thing as a ticked file or folder.
    /// </summary>
    public IReadOnlyList<SelectedItem> WithoutNested()
    {
        var items = _items.Values.ToList();
        var whole = items.Where(DeletesWholePath).ToList();
        return items.Where(item => !whole.Any(cover => !ReferenceEquals(cover, item) && Covers(cover, item))).ToList();
    }

    private static bool DeletesWholePath(SelectedItem item) =>
        item.Finding is null || (item.Finding.Action == FindingAction.Delete && !item.Finding.ContentsOnly && item.Finding.Files is null);

    private static bool Covers(SelectedItem cover, SelectedItem item)
    {
        if (!LongPath.IsSameOrInside(item.Path, cover.Path)) return false;
        if (item.Path.Length > Path.TrimEndingDirectorySeparator(cover.Path).Length) return true;
        // Same path picked twice: keep the file/folder tick, drop the Smart find.
        return cover.Finding is null && item.Finding is not null;
    }

    private void Notify()
    {
        Raise(nameof(Count));
        Raise(nameof(TotalSize));
        Raise(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
