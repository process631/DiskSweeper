using System.ComponentModel;
using System.Diagnostics;
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
    Overview,
    Folders,
    Files,
    Treemap,
    Types,
    Cleanup,
    Changes,
    History,
    Settings,
}

/// <summary>One entry of the sidebar.</summary>
public sealed class NavItem(MainTab tab, string title, string glyph) : ObservableObject
{
    private string _badge = "";

    public MainTab Tab { get; } = tab;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    /// <summary>Small text on the right, e.g. how much Cleanup can free.</summary>
    public string Badge
    {
        get => _badge;
        set
        {
            if (Set(ref _badge, value)) Raise(nameof(HasBadge));
        }
    }

    public bool HasBadge => _badge.Length > 0;
}

public sealed class MainViewModel : ObservableObject
{
    private static readonly Dictionary<MainTab, (string Title, string Subtitle)> Pages = new()
    {
        [MainTab.Overview] = ("Overview", "What's using space, at a glance."),
        [MainTab.Folders] = ("Folders", "Every folder and file, biggest first. Tick anything you want to remove."),
        [MainTab.Files] = ("Largest files", "The biggest files anywhere in the scan."),
        [MainTab.Treemap] = ("Treemap", "Each block is a file, sized by the space it uses. Double-click a folder to zoom in."),
        [MainTab.Types] = ("File types", "Which kinds of files take the most space."),
        [MainTab.Cleanup] = ("Cleanup", "Junk, leftovers of uninstalled games and programs, and old downloads."),
        [MainTab.Changes] = ("Changes", "What grew or shrank since an earlier scan."),
        [MainTab.History] = ("Deletion history", "Everything DiskSweeper has deleted, newest first."),
        [MainTab.Settings] = ("Settings", "How DiskSweeper scans and deletes."),
    };

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _progressTimer;
    private ScanResult? _scan;
    private ScanProgress? _progress;
    private CancellationTokenSource? _cancel;
    private string _scanPath;
    private bool _isScanning;
    private bool _isDeleting;
    private string _notice = "";
    private bool _noticeIsError;
    private double _progressValue;
    private bool _progressIndeterminate;
    private string _progressText = "";
    private string _scanInfo = "Not scanned yet";
    private MainTab _selectedTab;

    public MainViewModel(AppSettings settings, string? startPath, DeletionLog? log = null)
    {
        _settings = settings;
        _scanPath = startPath ?? settings.LastPath;

        Selection = new SelectionViewModel();
        // Ticks can come from code (e.g. "Select all safe"), so re-check button states on every change.
        Selection.Changed += (_, _) => CommandManager.InvalidateRequerySuggested();
        Tree = new FolderTreeViewModel(Selection);
        BiggestFiles = new BiggestFilesViewModel(Selection);
        FileTypes = new FileTypesViewModel();
        Treemap = new TreemapViewModel();
        SmartFinds = new SmartFindsViewModel(Selection);
        Changes = new ChangesViewModel();
        Overview = new OverviewViewModel();
        Details = new NodeDetailsViewModel(Selection);
        log ??= new DeletionLog();
        Log = new LogViewModel(log);
        Coordinator = new DeleteCoordinator(new DeleteService(log));

        // The details panel follows whatever was clicked last in the folder tree or the file list.
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderTreeViewModel.SelectedRow) && Tree.SelectedRow is TreeRowViewModel row) Details.Node = row.Node;
        };
        BiggestFiles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BiggestFilesViewModel.SelectedRow) && BiggestFiles.SelectedRow is FileRowViewModel row) Details.Node = row.Node;
        };
        SmartFinds.PropertyChanged += OnSmartFindsChanged;

        NavItems =
        [
            new(MainTab.Overview, "Overview", Icons.Home),
            new(MainTab.Folders, "Folders", Icons.Folder),
            new(MainTab.Files, "Largest files", Icons.File),
            new(MainTab.Treemap, "Treemap", Icons.Tiles),
            new(MainTab.Types, "File types", Icons.Pie),
            new(MainTab.Cleanup, "Cleanup", Icons.Broom),
            new(MainTab.Changes, "Changes", Icons.Trending),
            new(MainTab.History, "History", Icons.History),
        ];
        SettingsNav = new NavItem(MainTab.Settings, "Settings", Icons.Settings);

        ScanCommand = new AsyncCommand(ScanAsync, () => !IsScanning && !IsDeleting);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsScanning || IsDeleting);
        BrowseCommand = new RelayCommand(Browse, () => !IsScanning);
        DeleteSelectedCommand = new AsyncCommand(DeleteSelectedAsync, () => Selection.Count > 0 && !IsScanning && !IsDeleting);
        FindCommand = new AsyncCommand(() => SmartFinds.FindAsync(_scan), () => !SmartFinds.IsBusy && !IsScanning);
        CleanSafeCommand = new AsyncCommand(CleanSafeAsync, () => SmartFinds.HasSafe && !IsScanning && !IsDeleting);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        NavigateCommand = new RelayCommand(p => { if (p is MainTab tab) SelectedTab = tab; else if (p is string s && Enum.TryParse(s, out MainTab parsed)) SelectedTab = parsed; });
        DismissNoticeCommand = new RelayCommand(() => Notice = "");
        OpenDataFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(ProtectedPaths.AppDataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProtectedPaths.AppDataFolder}\"") { UseShellExecute = true });
        });
        ShowInExplorerCommand = new RelayCommand(p => { if (PathOf(p) is string path) Shell.ShowInExplorer(path); });
        CopyPathCommand = new RelayCommand(p => { if (PathOf(p) is string path) Shell.CopyText(path); });
        ShowInTreemapCommand = new RelayCommand(p => { if (NodeOf(p) is FileNode node) ShowInTreemap(node); });
        ShowInFoldersCommand = new RelayCommand(p => { if (NodeOf(p) is FileNode node) ShowInFolders(node); });
        InspectCommand = new RelayCommand(p => { if (NodeOf(p) is FileNode node) Details.Node = node; });
        ToggleSelectCommand = new RelayCommand(p => ToggleSelect(p));
        TreemapUpCommand = new RelayCommand(Treemap.GoUp, () => Treemap.CanGoUp);
        ShowExtensionCommand = new RelayCommand(p =>
        {
            if (p is ExtensionRow row)
            {
                BiggestFiles.ShowExtension(row.Extension == "(no extension)" ? "" : row.Extension);
                SelectedTab = MainTab.Files;
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

    public OverviewViewModel Overview { get; }

    public NodeDetailsViewModel Details { get; }

    public LogViewModel Log { get; }

    public DeleteCoordinator Coordinator { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    public NavItem SettingsNav { get; }

    public IReadOnlyList<string> Drives { get; }

    public ICommand ScanCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand FindCommand { get; }

    public ICommand CleanSafeCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public ICommand NavigateCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand ShowInExplorerCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand ShowInTreemapCommand { get; }

    public ICommand ShowInFoldersCommand { get; }

    public ICommand InspectCommand { get; }

    public ICommand ToggleSelectCommand { get; }

    public ICommand TreemapUpCommand { get; }

    public ICommand ShowExtensionCommand { get; }

    public bool IsElevated => Elevation.IsElevated;

    public string WindowTitle => IsElevated ? "DiskSweeper (Administrator)" : "DiskSweeper";

    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string DataFolder => ProtectedPaths.AppDataFolder;

    public ScanResult? Scan => _scan;

    public bool HasScan => _scan is not null;

    public MainTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            Raise(nameof(PageTitle));
            Raise(nameof(PageSubtitle));
            Raise(nameof(SelectedNav));
            Raise(nameof(IsSettingsSelected));
            if (value == MainTab.History) Log.Refresh();
            if (value == MainTab.Cleanup && !SmartFinds.HasRun && !SmartFinds.IsBusy && HasScan) FindCommand.Execute(null);
        }
    }

    /// <summary>The sidebar's highlighted entry (null while Settings, which sits apart at the bottom, is open).</summary>
    public NavItem? SelectedNav
    {
        get => NavItems.FirstOrDefault(n => n.Tab == _selectedTab);
        set
        {
            if (value is not null) SelectedTab = value.Tab;
        }
    }

    public bool IsSettingsSelected
    {
        get => _selectedTab == MainTab.Settings;
        set
        {
            if (value) SelectedTab = MainTab.Settings;
        }
    }

    public string PageTitle => Pages[_selectedTab].Title;

    public string PageSubtitle => Pages[_selectedTab].Subtitle;

    public string ScanPath
    {
        get => _scanPath;
        set => Set(ref _scanPath, value);
    }

    public bool UseFastScan
    {
        get => _settings.FastScan;
        set
        {
            _settings.FastScan = value;
            _settings.Save();
            Raise();
        }
    }

    public bool SaveScans
    {
        get => _settings.SaveScans;
        set
        {
            _settings.SaveScans = value;
            _settings.Save();
            Raise();
        }
    }

    /// <summary>Default choice in the delete dialog.</summary>
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

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public bool IsDeleting
    {
        get => _isDeleting;
        private set
        {
            if (Set(ref _isDeleting, value)) Raise(nameof(IsBusy));
        }
    }

    public bool IsBusy => _isDeleting;

    /// <summary>A message shown in a bar above the page (scan cancelled, errors, what a delete did…).</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value)) Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice.Length > 0;

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => Set(ref _noticeIsError, value);
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

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>"Scanned today at 11:34 PM · fast scan, 4.4 s" for the sidebar.</summary>
    public string ScanInfo
    {
        get => _scanInfo;
        private set => Set(ref _scanInfo, value);
    }

    /// <summary>Shows the last saved scan of the start folder right away, so the window isn't empty while a fresh scan runs.</summary>
    public async Task LoadLastScanAsync()
    {
        try
        {
            var last = ScanStore.List(ScanPath).FirstOrDefault();
            if (last is null) return;
            var result = await Task.Run(() => ScanStore.Load(last.File));
            ShowScan(result);
            FindCommand.Execute(null);
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
            ShowNotice("That doesn't look like a drive or folder. Pick one from the list or use Browse.", error: true);
            return;
        }

        Notice = "";
        _cancel = new CancellationTokenSource();
        _progress = new ScanProgress();
        IsScanning = true;
        ProgressValue = 0;
        ProgressText = $"Scanning {path}…";
        _progressTimer.Start();

        try
        {
            bool fast = UseFastScan && MftScanner.CanScan(path, out _);
            var result = await (fast
                ? new MftScanner().ScanAsync(path, _progress, _cancel.Token)
                : new DiskScanner().ScanAsync(path, _progress, _cancel.Token));

            if (_settings.SaveScans) await Task.Run(() => ScanStore.Save(result));
            _settings.LastPath = path;
            _settings.Save();

            Selection.Clear();
            ShowScan(result);
            if (result.UnreadableFolders.Count > 0)
            {
                ShowNotice($"{result.UnreadableFolders.Count:N0} folders couldn't be read, so their contents aren't counted."
                    + (IsElevated ? "" : " Run as administrator to include them."), error: false);
            }

            await Changes.LoadAsync(result);
            FindCommand.Execute(null);
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Scan cancelled.", error: false);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowNotice(ex.Message, error: true);
        }
        finally
        {
            _progressTimer.Stop();
            IsScanning = false;
            ProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }

    /// <summary>Puts a finished (or saved) scan on every page.</summary>
    public void ShowScan(ScanResult result)
    {
        _scan = result;
        _scanPath = result.Root.Name;
        Raise(nameof(ScanPath));
        Raise(nameof(HasScan));
        Tree.Load(result.Root);
        BiggestFiles.Load(result);
        FileTypes.Load(result);
        Overview.Load(result);
        Treemap.Root = result.Root;
        Details.Node = result.Root.Children.FirstOrDefault();
        ScanInfo = result.Method switch
        {
            ScanMethod.Saved => $"Saved scan from {Describe(result.ScannedAtUtc)}",
            ScanMethod.MasterFileTable => $"Scanned {Describe(result.ScannedAtUtc)} · fast scan, {result.Duration.TotalSeconds:0.0} s",
            _ => $"Scanned {Describe(result.ScannedAtUtc)} · {result.Duration.TotalSeconds:0.0} s",
        };
    }

    private void UpdateProgress()
    {
        if (_progress is null) return;
        if (_progress.Fraction is double fraction)
        {
            ProgressIndeterminate = false;
            ProgressValue = fraction * 100;
            ProgressText = $"Reading the drive's file table… {fraction:P0}";
        }
        else
        {
            ProgressIndeterminate = true;
            ProgressText = $"Scanning… {_progress.FilesScanned:N0} files · {_progress.FoldersScanned:N0} folders · {ByteSize.Format(_progress.BytesScanned)}";
        }
    }

    private async Task CleanSafeAsync()
    {
        SmartFinds.SelectSafe();
        await DeleteSelectedAsync();
    }

    private async Task DeleteSelectedAsync()
    {
        var draft = Coordinator.Plan(Selection.WithoutNested(), _settings.Method, dryRun: false);
        if (draft.ToDelete.Count == 0)
        {
            Dialogs.Info("Everything selected is protected, so there's nothing to delete:\n\n" + string.Join("\n", draft.Refused.Select(r => $"• {r.Path}: {r.Refusal}")));
            return;
        }

        var confirm = new ConfirmDeleteViewModel(draft);
        if (new Views.ConfirmDeleteWindow(confirm) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return;
        var plan = confirm.ToPlan();

        IsDeleting = true;
        _cancel = new CancellationTokenSource();
        ProgressIndeterminate = true;
        List<DeleteResultRow> rows;
        try
        {
            rows = await Coordinator.ExecuteAsync(plan, new Progress<string>(message => ProgressText = message), _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Stopped.", error: false);
            IsDeleting = false;
            return;
        }

        var results = new DeleteResultsViewModel(rows, Coordinator, plan);
        new Views.DeleteResultsWindow(results) { Owner = Application.Current.MainWindow }.ShowDialog();

        if (!plan.DryRun) await ApplyResultsAsync(rows);
        IsDeleting = false;
        ProgressIndeterminate = false;
        ShowNotice(results.Summary, error: !results.AllGood);
    }

    /// <summary>Takes deleted items out of the scanned tree and refreshes every page, without a full rescan.</summary>
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
            Overview.Load(_scan);
            Treemap.Invalidate();
            Details.Refresh();
        }
        Log.Refresh();
    }

    private void OnSmartFindsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SmartFindsViewModel.SafeBytes) or nameof(SmartFindsViewModel.HasRun))) return;
        var cleanup = NavItems.First(n => n.Tab == MainTab.Cleanup);
        cleanup.Badge = SmartFinds.SafeBytes > 0 ? SmartFinds.SafeText : "";
        CommandManager.InvalidateRequerySuggested();
    }

    private void ShowNotice(string message, bool error)
    {
        NoticeIsError = error;
        Notice = message;
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a drive or folder to scan", InitialDirectory = Directory.Exists(ScanPath) ? ScanPath : null };
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
        Details.Node = node;
        SelectedTab = MainTab.Treemap;
    }

    private void ShowInFolders(FileNode node)
    {
        Tree.Reveal(node);
        Details.Node = node;
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

    private static string Describe(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? $"today at {local:t}"
            : local.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {local:t}"
            : local.ToString("d MMM yyyy");
    }

    private static FileNode? NodeOf(object? parameter) => parameter switch
    {
        FileNode node => node,
        TreeRowViewModel row => row.Node,
        FileRowViewModel row => row.Node,
        TopEntry entry => entry.Node,
        _ => null,
    };

    private static string? PathOf(object? parameter) => parameter switch
    {
        FindingRowViewModel row => row.Path,
        ChangeRow row => row.Path.TrimEnd('\\'),
        LogRow row => row.Path,
        SelectedItem item => item.Path,
        _ => NodeOf(parameter)?.FullPath,
    };
}
