using System.IO;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Analysis;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App.ViewModels;

/// <summary>A slice of the "what's using space" bar: one kind of file and its share.</summary>
public sealed record KindShare(FileKind Kind, string Name, string SizeText, double Weight, string PercentText);

/// <summary>A clickable line in a "largest …" list.</summary>
public sealed record TopEntry(FileNode Node, string Name, string Detail, string SizeText, double Weight, string Glyph);

/// <summary>The Overview page: drive usage, what kinds of files fill it, and the biggest folders and files.</summary>
public sealed class OverviewViewModel : ObservableObject
{
    private const int KindsShown = 6;

    private string _driveTitle = "";
    private string _driveUsedText = "";
    private string _driveFreeText = "";
    private double _driveUsedPercent;
    private bool _hasDrive;
    private string _totalText = "";
    private string _countsText = "";

    public BulkObservableCollection<KindShare> Kinds { get; } = [];

    public BulkObservableCollection<TopEntry> Folders { get; } = [];

    public BulkObservableCollection<TopEntry> Files { get; } = [];

    public bool HasDrive
    {
        get => _hasDrive;
        private set => Set(ref _hasDrive, value);
    }

    public string DriveTitle
    {
        get => _driveTitle;
        private set => Set(ref _driveTitle, value);
    }

    public string DriveUsedText
    {
        get => _driveUsedText;
        private set => Set(ref _driveUsedText, value);
    }

    public string DriveFreeText
    {
        get => _driveFreeText;
        private set => Set(ref _driveFreeText, value);
    }

    public double DriveUsedPercent
    {
        get => _driveUsedPercent;
        private set => Set(ref _driveUsedPercent, value);
    }

    /// <summary>"540.1 GB" — everything the scan found.</summary>
    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    public string CountsText
    {
        get => _countsText;
        private set => Set(ref _countsText, value);
    }

    public void Load(ScanResult scan)
    {
        var root = scan.Root;
        TotalText = ByteSize.Format(root.Size);
        CountsText = $"{root.FileCount:N0} files in {root.FolderCount:N0} folders";
        LoadDrive(root.Name);

        // What kinds of files: the biggest few, then everything else together.
        var kinds = FileTypes.ByKind(FileTypes.ByExtension(root));
        double total = Math.Max(1, root.Size);
        var shares = kinds.Take(KindsShown)
            .Select(k => new KindShare(k.Kind, FileTypes.DisplayName(k.Kind), ByteSize.Format(k.Size), k.Size, $"{k.Size / total:P0}"))
            .ToList();
        long rest = kinds.Skip(KindsShown).Sum(k => k.Size);
        if (rest > 0) shares.Add(new KindShare(FileKind.Other, "Everything else", ByteSize.Format(rest), rest, $"{rest / total:P0}"));
        Kinds.ReplaceAll(shares);

        var folders = LargestFolders(root, 8);
        double biggestFolder = Math.Max(1, folders.FirstOrDefault()?.Size ?? 1);
        Folders.ReplaceAll(folders.Select(f => new TopEntry(f, f.Name, f.Parent?.FullPath ?? "", ByteSize.Format(f.Size), f.Size / biggestFolder, Icons.Folder)));

        var files = scan.GetLargestFiles(6);
        double biggestFile = Math.Max(1, files.FirstOrDefault()?.Size ?? 1);
        Files.ReplaceAll(files.Select(f => new TopEntry(f, f.Name, f.Parent?.FullPath ?? "", ByteSize.Format(f.Size), f.Size / biggestFile, Icons.File)));
    }

    /// <summary>
    /// The folders worth looking at: big ones, skipping "pass-through" folders where almost everything sits in
    /// one subfolder (C:\Users is mostly C:\Users\you, so only the latter is interesting).
    /// </summary>
    internal static List<FileNode> LargestFolders(FileNode root, int count)
    {
        long minimum = root.Size / 200;
        var candidates = new List<FileNode>();
        var queue = new Queue<(FileNode Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            foreach (var child in node.Children)
            {
                if (!child.IsDirectory || child.IsLink || child.Size < minimum) continue;
                if (depth < 4) queue.Enqueue((child, depth + 1));

                var biggestInside = child.Children.FirstOrDefault(c => c.IsDirectory);
                bool passThrough = biggestInside is not null && biggestInside.Size >= child.Size * 0.7;
                if (!passThrough) candidates.Add(child);
            }
        }
        return candidates.OrderByDescending(c => c.Size).Take(count).ToList();
    }

    private void LoadDrive(string path)
    {
        try
        {
            if (Path.GetPathRoot(path) is string driveRoot && new DriveInfo(driveRoot) is { IsReady: true } drive)
            {
                string letter = driveRoot.TrimEnd('\\');
                DriveTitle = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Local Disk ({letter})" : $"{drive.VolumeLabel} ({letter})";
                long used = drive.TotalSize - drive.TotalFreeSpace;
                DriveUsedText = $"{ByteSize.Format(used)} used";
                DriveFreeText = $"{ByteSize.Format(drive.TotalFreeSpace)} free of {ByteSize.Format(drive.TotalSize)}";
                DriveUsedPercent = 100.0 * used / Math.Max(1, drive.TotalSize);
                HasDrive = true;
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }
        HasDrive = false;
    }
}

/// <summary>The details panel: everything about the file or folder you clicked, with its actions.</summary>
public sealed class NodeDetailsViewModel : ObservableObject
{
    private readonly SelectionViewModel _selection;
    private FileNode? _node;

    public NodeDetailsViewModel(SelectionViewModel selection)
    {
        _selection = selection;
        selection.Changed += (_, _) =>
        {
            Raise(nameof(IsSelected));
            Raise(nameof(SelectText));
        };
    }

    public FileNode? Node
    {
        get => _node;
        set
        {
            if (ReferenceEquals(_node, value)) return;
            _node = value;
            RaiseAll();
        }
    }

    public bool HasNode => _node is not null;

    public string Name => _node?.Name ?? "";

    public string FullPath => _node?.FullPath ?? "";

    public string Glyph => _node is null ? "" : _node.IsLink ? Icons.Link : _node.IsDirectory ? Icons.Folder : Icons.File;

    public string KindText => _node switch
    {
        null => "",
        { IsLink: true } => "Link to another folder (not counted here)",
        { IsDirectory: true } => "Folder",
        _ => $"{FileTypes.DisplayName(FileTypes.KindOf(_node.Extension))}  ·  {(_node.Extension.Length > 0 ? _node.Extension.ToUpperInvariant()[1..] + " file" : "file")}",
    };

    public string SizeText => _node is null ? "" : ByteSize.Format(_node.Size);

    public string ContentsText => _node is { IsDirectory: true } ? $"{_node.FileCount:N0} files · {_node.FolderCount:N0} folders" : "";

    public bool HasContents => _node is { IsDirectory: true, IsLink: false };

    public string ModifiedText => _node is null || _node.LastWriteTimeUtc < DateTime.MinValue.AddYears(1) ? ""
        : (_node.IsDirectory ? "Newest change inside: " : "Last changed: ") + _node.LastWriteTimeUtc.ToLocalTime().ToString("d MMM yyyy");

    public double SharePercent => _node?.Parent is { Size: > 0 } parent ? 100.0 * _node.Size / parent.Size : 100;

    public string ShareText => _node?.Parent is FileNode parent ? $"{SharePercent:0.#}% of {parent.Name.TrimEnd('\\')}" : "The whole scan";

    public string? ProtectedReason => _node is null ? null : _node.Parent is null ? "This is the scanned folder itself." : ProtectedPaths.WhyProtected(_node.FullPath);

    public bool IsProtected => ProtectedReason is not null;

    public IReadOnlyList<string> Warnings => _node is null || IsProtected ? [] : PathWarnings.For(_node.FullPath);

    public bool HasWarnings => Warnings.Count > 0;

    public IReadOnlyList<TopEntry> BiggestInside
    {
        get
        {
            if (_node is not { IsDirectory: true } folder) return [];
            double biggest = Math.Max(1, folder.Children.FirstOrDefault()?.Size ?? 1);
            return folder.Children.Take(6)
                .Select(c => new TopEntry(c, c.Name, "", ByteSize.Format(c.Size), c.Size / biggest, c.IsDirectory ? Icons.Folder : Icons.File))
                .ToList();
        }
    }

    public bool IsSelected
    {
        get => _node is not null && _selection.Contains(_node.FullPath);
        set
        {
            if (_node is not null && !IsProtected) _selection.Set(SelectedItem.From(_node), value);
        }
    }

    public string SelectText => IsSelected ? "Selected for cleanup" : "Select for cleanup";

    /// <summary>Call after the tree changed (e.g. something was deleted) so sizes and lists are current.</summary>
    public void Refresh()
    {
        if (_node is not null && !FileSystemWalk.Exists(_node.FullPath)) _node = null;
        RaiseAll();
    }
}
