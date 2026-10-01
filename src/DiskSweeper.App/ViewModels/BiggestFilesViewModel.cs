using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Analysis;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App.ViewModels;

public sealed class FileRowViewModel(FileNode node, SelectionViewModel selection) : ObservableObject
{
    public FileNode Node { get; } = node;

    public string Name => Node.Name;

    public long Size => Node.Size;

    public string SizeText => ByteSize.Format(Node.Size);

    public DateTime Modified => Node.LastWriteTimeUtc.ToLocalTime();

    public FileKind Kind => FileTypes.KindOf(Node.Extension);

    public string KindName => FileTypes.DisplayName(Kind);

    public string Folder => Node.Parent?.FullPath ?? "";

    public bool CanSelect => ProtectedPaths.WhyProtected(Node.FullPath) is null;

    public string? ProtectedReason => ProtectedPaths.WhyProtected(Node.FullPath);

    public bool IsChecked
    {
        get => selection.Contains(Node.FullPath);
        set => selection.Set(SelectedItem.From(Node), value);
    }

    public void RefreshChecked() => Raise(nameof(IsChecked));
}

/// <summary>The Biggest files tab: the largest files anywhere in the scan, with filters.</summary>
public sealed class BiggestFilesViewModel : ObservableObject
{
    public const string AnyKind = "Any type";

    private readonly SelectionViewModel _selection;
    private ScanResult? _scan;
    private string _minimumSize = "10 MB";
    private int _olderThanDays;
    private string _kind = AnyKind;
    private string _search = "";
    private string _summary = "";
    private FileRowViewModel? _selectedRow;

    public BiggestFilesViewModel(SelectionViewModel selection)
    {
        _selection = selection;
        selection.Changed += (_, _) =>
        {
            foreach (var row in Rows) row.RefreshChecked();
        };
    }

    public BulkObservableCollection<FileRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> Kinds { get; } = [AnyKind, .. Enum.GetValues<FileKind>().Select(FileTypes.DisplayName)];

    public IReadOnlyList<KeyValuePair<int, string>> AgeChoices { get; } =
    [
        new(0, "Any age"), new(30, "Not changed for 30+ days"), new(90, "Not changed for 90+ days"),
        new(180, "Not changed for 6+ months"), new(365, "Not changed for 1+ year"),
    ];

    public string MinimumSize
    {
        get => _minimumSize;
        set
        {
            if (Set(ref _minimumSize, value)) Refresh();
        }
    }

    public int OlderThanDays
    {
        get => _olderThanDays;
        set
        {
            if (Set(ref _olderThanDays, value)) Refresh();
        }
    }

    public string Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value)) Refresh();
        }
    }

    /// <summary>Matches file names; ".mp4" style text matches the extension exactly.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) Refresh();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public FileRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => Set(ref _selectedRow, value);
    }

    public void Load(ScanResult scan)
    {
        _scan = scan;
        Refresh();
    }

    public void ShowExtension(string extension)
    {
        _kind = AnyKind;
        Raise(nameof(Kind));
        Search = extension;
    }

    public void Refresh()
    {
        if (_scan is null) return;

        long minimum = ByteSize.TryParse(MinimumSize, out long parsed) ? parsed : 0;
        var cutoff = DateTime.UtcNow.AddDays(-OlderThanDays);
        FileKind? kind = Enum.GetValues<FileKind>().Cast<FileKind?>().FirstOrDefault(k => FileTypes.DisplayName(k!.Value) == Kind);
        string search = Search.Trim();
        bool extensionSearch = search.StartsWith('.') && !search.Contains(' ');

        bool Matches(FileNode file) =>
            file.Size >= minimum
            && (OlderThanDays == 0 || file.LastWriteTimeUtc < cutoff)
            && (kind is null || FileTypes.KindOf(file.Extension) == kind)
            && (search.Length == 0 || (extensionSearch
                ? file.Extension.Equals(search, StringComparison.OrdinalIgnoreCase)
                : file.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));

        var files = _scan.GetLargestFiles(1000, Matches);
        Rows.ReplaceAll(files.Select(f => new FileRowViewModel(f, _selection)));
        Summary = files.Count == 0
            ? "No files match these filters."
            : $"{files.Count:N0} files · {ByteSize.Format(files.Sum(f => f.Size))}";
    }
}

public sealed record KindRow(FileKind Kind, string Size, long Bytes, long Count, double BarWidth)
{
    public string KindName => FileTypes.DisplayName(Kind);
}

public sealed record ExtensionRow(string Extension, FileKind Kind, string Size, long Bytes, long Count, double BarWidth)
{
    public string KindName => FileTypes.DisplayName(Kind);
}

/// <summary>The File types tab: what kinds of files fill the disk.</summary>
public sealed class FileTypesViewModel : ObservableObject
{
    public BulkObservableCollection<KindRow> Kinds { get; } = [];

    public BulkObservableCollection<ExtensionRow> Extensions { get; } = [];

    public void Load(ScanResult scan)
    {
        var extensions = FileTypes.ByExtension(scan.Root);
        var kinds = FileTypes.ByKind(extensions);

        // Bars are relative to the biggest entry, so the top one is full width and the rest compare to it.
        double biggestKind = Math.Max(1, kinds.FirstOrDefault()?.Size ?? 1);
        double biggestExtension = Math.Max(1, extensions.FirstOrDefault()?.Size ?? 1);
        Kinds.ReplaceAll(kinds.Select(k => new KindRow(k.Kind, ByteSize.Format(k.Size), k.Size, k.Count, k.Size / biggestKind * 160)));
        Extensions.ReplaceAll(extensions.Take(500).Select(e =>
            new ExtensionRow(e.Extension, e.Kind, ByteSize.Format(e.Size), e.Size, e.Count, e.Size / biggestExtension * 160)));
    }
}

/// <summary>The Treemap tab: which folder is shown and what's under the mouse.</summary>
public sealed class TreemapViewModel : ObservableObject
{
    private FileNode? _root;
    private FileNode? _hovered;
    private string _hoverText = "Hover over a block to see what it is. Double-click a folder to zoom in, right-click for options.";

    public FileNode? Root
    {
        get => _root;
        set
        {
            if (Set(ref _root, value))
            {
                Raise(nameof(Breadcrumb));
                Raise(nameof(CanGoUp));
            }
        }
    }

    public string Breadcrumb => Root is null ? "" : $"{Root.FullPath}  ·  {ByteSize.Format(Root.Size)}";

    public bool CanGoUp => Root?.Parent is not null;

    public string HoverText
    {
        get => _hoverText;
        set => Set(ref _hoverText, value);
    }

    public void GoUp()
    {
        if (Root?.Parent is FileNode parent) Root = parent;
    }

    /// <summary>Set by the treemap as the mouse moves.</summary>
    public FileNode? HoveredNode
    {
        get => _hovered;
        set
        {
            _hovered = value;
            Describe(value);
        }
    }

    public void Describe(FileNode? node)
    {
        if (node is null) return;
        string kind = node.IsDirectory ? $"folder · {node.FileCount:N0} files" : FileTypes.DisplayName(FileTypes.KindOf(node.Extension)).ToLowerInvariant();
        HoverText = $"{node.FullPath}  ·  {ByteSize.Format(node.Size)}  ·  {kind}";
    }

    /// <summary>Bumped whenever the tree changes underneath the treemap, so it lays itself out again.</summary>
    public int Version { get; private set; }

    public void Invalidate()
    {
        Version++;
        Raise(nameof(Version));
        Raise(nameof(Breadcrumb));
    }
}
