using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Analysis;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App.ViewModels;

public enum MainTab
{
    Folders,
    BiggestFiles,
    FileTypes,
    Treemap,
    SmartFinds,
    Changes,
    Log,
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _progressTimer;
    private ScanResult? _scan;
    private ScanProgress? _progress;
    private CancellationTokenSource? _cancel;
    private string _scanPath;
    private bool _isScanning;
    private bool _isDeleting;
    private string _status = "Pick a drive or folder and press Scan.";
    private double _progressValue;
    private bool _progressIndeterminate;
    private string _summary = "";
    private MainTab _selectedTab;

    public MainViewModel(AppSettings settings, string? startPath, DeletionLog? log = null)
    {
        _settings = settings;
        _scanPath = startPath ?? settings.LastPath;

        Selection = new SelectionViewModel();
        // Ticks can come from code (e.g. "Select everything marked Safe"), so re-check button states on every change.
        Selection.Changed += (_, _) => CommandManager.InvalidateRequerySuggested();
        Tree = new FolderTreeViewModel(Selection);
        BiggestFiles = new BiggestFilesViewModel(Selection);
        FileTypes = new FileTypesViewModel();
        Treemap = new TreemapViewModel();
        SmartFinds = new SmartFindsViewModel(Selection);
        Changes = new ChangesViewModel();
        log ??= new DeletionLog();
        Log = new LogViewModel(log);
        Coordinator = new DeleteCoordinator(new DeleteService(log));

        ScanCommand = new AsyncCommand(ScanAsync, () => !IsScanning && !IsDeleting);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsScanning || SmartFinds.IsBusy);
        BrowseCommand = new RelayCommand(Browse, () => !IsScanning);
        DeleteSelectedCommand = new AsyncCommand(DeleteSelectedAsync, () => Selection.Count > 0 && !IsScanning && !IsDeleting);
        FindCommand = new AsyncCommand(() => SmartFinds.FindAsync(_scan), () => !SmartFinds.IsBusy && !IsScanning);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        ShowInExplorerCommand = new RelayCommand(p => { if (PathOf(p) is string path) Shell.ShowInExplorer(path); });
        CopyPathCommand = new RelayCommand(p => { if (PathOf(p) is string path) Shell.CopyText(path); });
        ShowInTreemapCommand = new RelayCommand(p => { if (NodeOf(p) is FileNode node) ShowInTreemap(node); });
        ShowInFoldersCommand = new RelayCommand(p => { if (NodeOf(p) is FileNode node) ShowInFolders(node); });
        ToggleSelectCommand = new RelayCommand(p => ToggleSelect(p));
        TreemapUpCommand = new RelayCommand(Treemap.GoUp, () => Treemap.CanGoUp);
        ShowExtensionCommand = new RelayCommand(p =>
        {
            if (p is ExtensionRow row)
            {
                BiggestFiles.ShowExtension(row.Extension == "(no extension)" ? "" : row.Extension);
                SelectedTab = MainTab.BiggestFiles;
            }
        });

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        Drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }

    public SelectionViewModel Selection { get; }

    public FolderTreeViewModel Tree { get; }

    public BiggestFilesViewModel BiggestFiles { get; }

    public FileTypesViewModel FileTypes { get; }

    public TreemapViewModel Treemap { get; }

    public SmartFindsViewModel SmartFinds { get; }

    public ChangesViewModel Changes { get; }

    public LogViewModel Log { get; }

    public DeleteCoordinator Coordinator { get; }

    public IReadOnlyList<string> Drives { get; }

    public ICommand ScanCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand FindCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public ICommand ShowInExplorerCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand ShowInTreemapCommand { get; }

    public ICommand ShowInFoldersCommand { get; }

    public ICommand ToggleSelectCommand { get; }

    public ICommand TreemapUpCommand { get; }

    public ICommand ShowExtensionCommand { get; }

    public bool IsElevated => Elevation.IsElevated;

    public string WindowTitle => IsElevated ? "DiskSweeper (Administrator)" : "DiskSweeper";

    public ScanResult? Scan => _scan;

    public string ScanPath
    {
        get => _scanPath;
        set
        {
            if (Set(ref _scanPath, value)) Raise(nameof(FastScanHint));
        }
    }

    public bool UseFastScan
    {
        get => _settings.FastScan;
        set
        {
            _settings.FastScan = value;
            Raise();
        }
    }

    public string FastScanHint => !IsElevated
        ? "Fast scan reads the NTFS file table directly. It needs administrator rights: use “Run as administrator”."
        : "Reads the NTFS file table directly: a whole drive in seconds. Used for whole NTFS drives; folders use the normal scan.";

    public bool UseRecycleBin
    {
        get => _settings.Method == DeleteMethod.RecycleBin;
        set
        {
            if (value) SetMethod(DeleteMethod.RecycleBin);
        }
    }

    public bool UsePermanent
    {
        get => _settings.Method == DeleteMethod.Permanent;
        set
        {
            if (value) SetMethod(DeleteMethod.Permanent);
        }
    }

    public bool DryRun { get; set; }

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public bool IsDeleting
    {
        get => _isDeleting;
        private set => Set(ref _isDeleting, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => Set(ref _progressIndeterminate, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool HasScan => _scan is not null;

    public MainTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            if (value == MainTab.Log) Log.Refresh();
            if (value == MainTab.SmartFinds && SmartFinds.Rows.Count == 0 && !SmartFinds.IsBusy) FindCommand.Execute(null);
        }
    }

    /// <summary>Shows the last saved scan of the start folder right away, so the window isn't empty while a fresh scan runs.</summary>
    public async Task LoadLastScanAsync()
    {
        try
        {
            var last = ScanStore.List(ScanPath).FirstOrDefault();
            if (last is null) return;
            var result = await Task.Run(() => ScanStore.Load(last.File));
            Show(result);
            Status = $"Showing the scan from {last.ScannedAtUtc.ToLocalTime():g}. Press Scan for fresh numbers.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }
    }

    private async Task ScanAsync()
    {
        string path;
        try
        {
            path = LongPath.Normalize(ScanPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            Status = "That doesn't look like a folder path.";
            return;
        }

        _cancel = new CancellationTokenSource();
        _progress = new ScanProgress();
        IsScanning = true;
        ProgressValue = 0;
        _progressTimer.Start();

        try
        {
            bool fast = UseFastScan && MftScanner.CanScan(path, out _);
            var scan = fast
                ? new MftScanner().ScanAsync(path, _progress, _cancel.Token)
                : new DiskScanner().ScanAsync(path, _progress, _cancel.Token);
            var result = await scan;

            if (_settings.SaveScans) await Task.Run(() => ScanStore.Save(result));
            _settings.LastPath = path;
            _settings.Save();

            Selection.Clear();
            Show(result);
            await Changes.LoadAsync(result);
            string method = result.Method == ScanMethod.MasterFileTable ? "fast NTFS read" : "folder scan";
            Status = $"Scanned {result.Root.Name} in {result.Duration.TotalSeconds:0.0} s ({method}).";

            if (SelectedTab == MainTab.SmartFinds) FindCommand.Execute(null);
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = ex.Message;
        }
        finally
        {
            _progressTimer.Stop();
            IsScanning = false;
            ProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }

    internal void Show(ScanResult result)
    {
        _scan = result;
        _scanPath = result.Root.Name;
        Raise(nameof(ScanPath));
        Raise(nameof(HasScan));
        Tree.Load(result.Root);
        BiggestFiles.Load(result);
        FileTypes.Load(result);
        Treemap.Root = result.Root;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_scan is null) return;
        var root = _scan.Root;
        string drive = "";
        try
        {
            if (Path.GetPathRoot(root.Name) is string driveRoot && new DriveInfo(driveRoot) is { IsReady: true } info)
                drive = $"  ·  Drive {driveRoot.TrimEnd('\\')} has {ByteSize.Format(info.TotalFreeSpace)} free of {ByteSize.Format(info.TotalSize)}";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
        }

        string notes = _scan.UnreadableFolders.Count > 0 ? $"  ·  {_scan.UnreadableFolders.Count:N0} folders couldn't be read" : "";
        Summary = $"{root.Name}: {ByteSize.Format(root.Size)} in {root.FileCount:N0} files and {root.FolderCount:N0} folders{drive}{notes}";
    }

    private void UpdateProgress()
    {
        if (_progress is null) return;
        if (_progress.Fraction is double fraction)
        {
            ProgressIndeterminate = false;
            ProgressValue = fraction * 100;
            Status = $"Reading the drive's file table… {fraction:P0}";
        }
        else
        {
            ProgressIndeterminate = true;
            Status = $"Scanning… {_progress.FilesScanned:N0} files, {_progress.FoldersScanned:N0} folders, {ByteSize.Format(_progress.BytesScanned)}";
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var plan = Coordinator.Plan(Selection.WithoutNested(), _settings.Method, DryRun);
        if (plan.ToDelete.Count == 0)
        {
            Dialogs.Info("Everything selected is protected, so there's nothing to delete:\n\n" + string.Join("\n", plan.Refused.Select(r => $"• {r.Path}: {r.Refusal}")));
            return;
        }

        var confirm = new Views.ConfirmDeleteWindow(plan) { Owner = Application.Current.MainWindow };
        if (confirm.ShowDialog() != true) return;

        IsDeleting = true;
        _cancel = new CancellationTokenSource();
        List<DeleteResultRow> rows;
        try
        {
            rows = await Coordinator.ExecuteAsync(plan, new Progress<string>(message => Status = message), _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
            IsDeleting = false;
            return;
        }

        var results = new DeleteResultsViewModel(rows, Coordinator, plan);
        new Views.DeleteResultsWindow(results) { Owner = Application.Current.MainWindow }.ShowDialog();

        Status = "Updating the view…";
        if (!plan.DryRun) await ApplyResultsAsync(rows);
        IsDeleting = false;
        Status = results.Summary;
    }

    /// <summary>Takes deleted items out of the scanned tree and refreshes every tab, without a full rescan.</summary>
    internal async Task ApplyResultsAsync(IReadOnlyList<DeleteResultRow> rows)
    {
        var cleanedFindings = new List<Core.SmartFinds.Finding>();
        var handledKeys = new List<string>();

        foreach (var row in rows)
        {
            if (row.AllSucceeded && row.Item.Finding is not null) cleanedFindings.Add(row.Item.Finding);
            if (row.AllSucceeded) handledKeys.Add(row.Item.Finding is not null ? "finding:" + row.Item.Finding.Id : row.Item.Path);

            foreach (var result in row.Results)
            {
                if (_scan?.FindNode(result.Path) is not FileNode node) continue;
                if (result.Outcome is DeleteOutcome.Deleted or DeleteOutcome.Recycled or DeleteOutcome.AlreadyGone)
                    TreeEditor.Remove(node);
                else if (result.Outcome == DeleteOutcome.Failed && result.Bytes > 0)
                    await TreeEditor.RefreshAsync(node);
            }
        }

        // Also untick things that vanished with a deleted parent folder.
        handledKeys.AddRange(Selection.Items.Where(i => i.Finding is null && !FileSystemWalk.Exists(i.Path)).Select(i => i.Key));
        Selection.Remove(handledKeys);
        SmartFinds.RemoveCleaned(cleanedFindings);
        if (_scan is not null)
        {
            Tree.Rebuild();
            BiggestFiles.Refresh();
            FileTypes.Load(_scan);
            Treemap.Invalidate();
            UpdateSummary();
        }
        Log.Refresh();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to scan", InitialDirectory = Directory.Exists(ScanPath) ? ScanPath : null };
        if (dialog.ShowDialog() == true)
        {
            ScanPath = dialog.FolderName;
            ScanCommand.Execute(null);
        }
    }

    private void RestartAsAdmin()
    {
        _settings.Save();
        if (ElevatedRunner.RestartElevated(ScanPath)) Application.Current.Shutdown();
    }

    private void ShowInTreemap(FileNode node)
    {
        Treemap.Root = node.IsDirectory ? node : node.Parent ?? node;
        SelectedTab = MainTab.Treemap;
    }

    private void ShowInFolders(FileNode node)
    {
        Tree.Reveal(node);
        SelectedTab = MainTab.Folders;
    }

    private void ToggleSelect(object? parameter)
    {
        if (NodeOf(parameter) is not FileNode node || ProtectedPaths.WhyProtected(node.FullPath) is not null) return;
        Selection.Set(SelectedItem.From(node), !Selection.Contains(node.FullPath));
    }

    private void SetMethod(DeleteMethod method)
    {
        _settings.Method = method;
        _settings.Save();
        Raise(nameof(UseRecycleBin));
        Raise(nameof(UsePermanent));
    }

    private static FileNode? NodeOf(object? parameter) => parameter switch
    {
        FileNode node => node,
        TreeRowViewModel row => row.Node,
        FileRowViewModel row => row.Node,
        _ => null,
    };

    private static string? PathOf(object? parameter) => parameter switch
    {
        FindingRowViewModel row => row.Path,
        ChangeRow row => row.Path.TrimEnd('\\'),
        LogRow row => row.Path,
        _ => NodeOf(parameter)?.FullPath,
    };
}
