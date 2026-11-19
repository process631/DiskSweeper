using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Programs;

namespace DiskSweeper.App.ViewModels;

/// <summary>One installed program in the search list.</summary>
public sealed class ProgramRow
{
    public required CatalogApp App { get; init; }

    public string Name => App.Name;

    public string Publisher => string.IsNullOrWhiteSpace(App.Publisher) ? "—" : App.Publisher!;

    public string Source => App.SourceLabel;

    public string SizeText => App.ListedSizeBytes is long size && size > 0 ? ByteSize.Format(size) : "—";

    public long SizeSort => App.ListedSizeBytes ?? -1;

    public string Location => App.InstallLocation ?? "";

    public string Version => string.IsNullOrWhiteSpace(App.Version) ? "" : App.Version;
}

/// <summary>One line of "what will be removed", with its own tick.</summary>
public sealed class RemovalRow : ObservableObject
{
    private bool _isChecked;

    public RemovalRow(RemovalItem item)
    {
        Item = item;
        _isChecked = item.SelectedByDefault;
    }

    public RemovalItem Item { get; }

    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? Item.Location : Item.Title;

    public string Location => Item.Location;

    public string Reason => Item.Refusal ?? Item.Reason;

    public string Kind => Item.Kind switch
    {
        RemovalKind.InstallFolder => "Install folder",
        RemovalKind.Shortcut => "Shortcut",
        RemovalKind.RegistryKey => "Registry key",
        _ => "Leftover",
    };

    public string SizeText => Item.Kind == RemovalKind.RegistryKey ? "" : ByteSize.Format(Item.SizeBytes);

    public bool CanSelect => Item.Refusal is null;

    public bool HasRefusal => Item.Refusal is not null;

    public string Glyph => Item.Kind switch
    {
        RemovalKind.Shortcut => Icons.Link,
        RemovalKind.RegistryKey => Icons.Lock,
        _ => Icons.Folder,
    };

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!CanSelect || !Set(ref _isChecked, value)) return;
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;
}

/// <summary>The confirmation before a program and its leftovers are removed.</summary>
public sealed class ConfirmProgramRemovalViewModel : ObservableObject
{
    private DeleteMethod _method;
    private bool _dryRun;
    private bool _extraConfirmed;
    private bool _closeFirst;

    public ConfirmProgramRemovalViewModel(
        ProgramRow program,
        IReadOnlyList<RemovalRow> chosen,
        UninstallLaunch? uninstall,
        IReadOnlyList<RunningPrograms.Snapshot> running,
        DeleteMethod method,
        bool dryRun)
    {
        Program = program;
        Chosen = chosen;
        Uninstall = uninstall;
        Running = running;
        _method = method;
        _dryRun = dryRun;
        _closeFirst = running.Count > 0;
    }

    public ProgramRow Program { get; }

    public IReadOnlyList<RemovalRow> Chosen { get; }

    public UninstallLaunch? Uninstall { get; }

    public IReadOnlyList<RunningPrograms.Snapshot> Running { get; }

    public string Heading => _dryRun ? $"Preview removing {Program.Name}?" : $"Delete {Program.Name}?";

    public string Subtitle
    {
        get
        {
            string source = string.IsNullOrWhiteSpace(Program.Version) ? Program.Source : $"{Program.Source} · {Program.Version}";
            return $"{source} · {Program.Publisher}";
        }
    }

    public string UninstallText => Uninstall switch
    {
        null => "No official uninstaller was found. Only the ticked items are removed.",
        { Style: UninstallStyle.Silent } => "The official uninstaller runs first, quietly. Leftovers that are still there are removed after it finishes.",
        { Style: UninstallStyle.LauncherHandoff } => "The launcher is asked to uninstall it first. You may need to confirm in that launcher, then leftovers that remain are removed.",
        _ => "The program's own uninstaller runs first. Finish that window if it appears. Leftovers that are still there are removed afterwards.",
    };

    public string CommandText => Uninstall?.DisplayText ?? "";

    public bool HasCommand => Uninstall is not null;

    public string CountText
    {
        get
        {
            int count = Chosen.Count;
            string items = count == 0 ? "No extra files" : count == 1 ? "1 item" : $"{count:N0} items";
            return $"{items} · {ByteSize.Format(TotalSize)}";
        }
    }

    public long TotalSize => Chosen.Where(row => row.Item.Kind != RemovalKind.RegistryKey).Sum(row => row.Item.SizeBytes);

    public bool HasRunning => Running.Count > 0;

    public string RunningText => "Still running: " + string.Join(", ", Running.Select(process => process.ProcessName).Distinct());

    public bool CloseFirst
    {
        get => _closeFirst;
        set => Set(ref _closeFirst, value);
    }

    public bool ShowAdminNote =>
        Program.App.MachineWide
        || (Program.App.UninstallKey is string key && RegistryKeys.NeedsAdmin(key))
        || Chosen.Any(row => row.Item.Kind == RemovalKind.RegistryKey && RegistryKeys.NeedsAdmin(row.Location));

    public bool UseRecycleBin
    {
        get => _method == DeleteMethod.RecycleBin;
        set { if (value) SetMethod(DeleteMethod.RecycleBin); }
    }

    public bool UsePermanent
    {
        get => _method == DeleteMethod.Permanent;
        set { if (value) SetMethod(DeleteMethod.Permanent); }
    }

    public bool DryRun
    {
        get => _dryRun;
        set
        {
            if (!Set(ref _dryRun, value)) return;
            _extraConfirmed = false;
            RaiseAll();
        }
    }

    public bool NeedsExtraConfirmation => !_dryRun && (_method == DeleteMethod.Permanent || TotalSize >= 10L << 30);

    public string ExtraConfirmationText => _method == DeleteMethod.Permanent
        ? $"I understand that {ByteSize.Format(TotalSize)} will be deleted permanently and can't be restored."
        : $"Yes, remove {Program.Name} and move {ByteSize.Format(TotalSize)} to the Recycle Bin.";

    public bool ExtraConfirmed
    {
        get => _extraConfirmed;
        set { if (Set(ref _extraConfirmed, value)) Raise(nameof(CanConfirm)); }
    }

    public bool CanConfirm => !NeedsExtraConfirmation || _extraConfirmed;

    public string ConfirmText => _dryRun ? "Run preview" : _method == DeleteMethod.RecycleBin ? "Delete and move files to the Recycle Bin" : "Delete permanently";

    public DeleteMethod Method => _method;

    private void SetMethod(DeleteMethod method)
    {
        if (_method == method) return;
        _method = method;
        _extraConfirmed = false;
        RaiseAll();
    }
}

/// <summary>Search installed programs and games, show what removal would touch, then uninstall and clean up.</summary>
public sealed class ProgramsViewModel : ObservableObject
{
    private readonly DeleteCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly List<ProgramRow> _all = [];
    private CancellationTokenSource? _detailCancel;
    private CancellationTokenSource? _removeCancel;
    private ProgramRow? _selected;
    private string _search = "";
    private string _status = "Programs and games haven't been loaded yet.";
    private string _note = "";
    private bool _loading;
    private bool _detailBusy;
    private bool _loaded;
    private bool _removing;
    private bool _filtering;

    public ProgramsViewModel(DeleteCoordinator coordinator, AppSettings settings)
    {
        _coordinator = coordinator;
        _settings = settings;
        Rows = [];
        Items = [];
        RefreshCommand = new AsyncCommand(LoadAsync, () => !_loading && !_removing);
        PreviewCommand = new AsyncCommand(() => RemoveAsync(preview: true), () => CanRemove);
        DeleteCommand = new AsyncCommand(() => RemoveAsync(preview: false), () => CanRemove);
        OpenLocationCommand = new RelayCommand(() => { if (Selected?.Location is { Length: > 0 } path) Shell.ShowInExplorer(path); }, () => Selected?.Location.Length > 0);
        CopyLocationCommand = new RelayCommand(() => { if (Selected?.Location is { Length: > 0 } path) Shell.CopyText(path); }, () => Selected?.Location.Length > 0);
    }

    public ObservableCollection<ProgramRow> Rows { get; }

    public ObservableCollection<RemovalRow> Items { get; }

    public ICommand RefreshCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand CopyLocationCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            ApplyFilter();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Note
    {
        get => _note;
        private set
        {
            if (Set(ref _note, value)) Raise(nameof(HasNote));
        }
    }

    public bool HasNote => _note.Length > 0;

    public bool IsLoading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public bool IsDetailBusy
    {
        get => _detailBusy;
        private set => Set(ref _detailBusy, value);
    }

    public bool IsRemoving
    {
        get => _removing;
        private set => Set(ref _removing, value);
    }

    public ProgramRow? Selected
    {
        get => _selected;
        set
        {
            // Rebuilding the list makes the grid clear its selection for a moment. Ignore that
            // so the detail pane doesn't reload when the same program is still selected.
            if (_filtering && value is null && _selected is not null) return;
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(IsThisApp));
            Raise(nameof(CanRemove));
            CommandManager.InvalidateRequerySuggested();
            _ = LoadDetailAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsThisApp => _selected is not null && ProgramCatalog.IsThisApplication(_selected.App);

    public bool CanRemove => _selected is not null && !_removing && !IsThisApp && (Items.Any(row => row.IsChecked) || UninstallCommand.TryCreate(_selected.App) is not null);

    public string TotalText => ByteSize.Format(Items.Where(row => row.IsChecked && row.Item.Kind != RemovalKind.RegistryKey).Sum(row => row.Item.SizeBytes));

    public string SelectionSummary
    {
        get
        {
            int count = Items.Count(row => row.IsChecked);
            if (_selected is null) return "";
            var uninstall = UninstallCommand.TryCreate(_selected.App);
            string how = uninstall is null ? "No official uninstaller." : uninstall.Style == UninstallStyle.Silent ? "Official uninstaller, quiet." : "Official uninstaller.";
            return count == 0 ? how : $"{count:N0} ticked · {TotalText}. {how}";
        }
    }

    public event Action<bool>? RemovingChanged;

    public event Action<string>? Progress;

    public event Action<string, bool>? Notify;

    public void EnsureLoaded()
    {
        if (_loaded || _loading) return;
        _ = LoadAsync();
    }

    public void CancelActive()
    {
        _detailCancel?.Cancel();
        _removeCancel?.Cancel();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        Status = "Looking up installed programs and games…";
        try
        {
            var apps = await Task.Run(ProgramCatalog.Load);
            _all.Clear();
            _all.AddRange(apps.Select(app => new ProgramRow { App = app }));
            _loaded = true;
            ApplyFilter();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyFilter()
    {
        string query = _search.Trim();
        IEnumerable<ProgramRow> matched = _all;
        if (query.Length > 0)
        {
            matched = _all.Where(row =>
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Source.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Location.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var rows = matched
            .OrderByDescending(row => query.Length > 0 && row.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? keep = _selected?.App.Id;
        _filtering = true;
        try
        {
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            var match = keep is null ? null : Rows.FirstOrDefault(row => row.App.Id == keep);
            if (ReferenceEquals(match, _selected))
                Raise(nameof(Selected));
            else
            {
                _filtering = false;
                Selected = match;
            }
        }
        finally
        {
            _filtering = false;
        }
        if (!_loaded) return;
        Status = _all.Count == 0
            ? "No installed programs were found."
            : query.Length == 0
                ? $"{_all.Count:N0} programs and games. Search for one, for example Fortnite or Roblox."
                : $"{rows.Count:N0} of {_all.Count:N0} match \"{query}\".";
    }

    private async Task LoadDetailAsync(ProgramRow? row)
    {
        _detailCancel?.Cancel();
        _detailCancel = new CancellationTokenSource();
        var token = _detailCancel.Token;
        foreach (var old in Items) old.CheckedChanged -= OnRowChecked;
        Items.Clear();
        Note = "";
        Raise(nameof(TotalText));
        Raise(nameof(SelectionSummary));
        Raise(nameof(CanRemove));
        if (row is null) return;

        IsDetailBusy = true;
        try
        {
            var plan = await Task.Run(() => RemovalScanner.Scan(row.App, _all.Select(item => item.App).ToList(), token), token);
            if (token.IsCancellationRequested) return;
            Note = plan.Note ?? "";
            foreach (var item in plan.Items.OrderByDescending(item => item.Kind == RemovalKind.RegistryKey ? -1 : item.SizeBytes))
            {
                var line = new RemovalRow(item);
                line.CheckedChanged += OnRowChecked;
                Items.Add(line);
            }
            Raise(nameof(TotalText));
            Raise(nameof(SelectionSummary));
            Raise(nameof(CanRemove));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested) IsDetailBusy = false;
        }
    }

    private void OnRowChecked(object? sender, EventArgs e)
    {
        Raise(nameof(TotalText));
        Raise(nameof(SelectionSummary));
        Raise(nameof(CanRemove));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task RemoveAsync(bool preview)
    {
        if (_selected is null || IsThisApp) return;
        var app = _selected.App;
        var chosen = Items.Where(row => row.IsChecked).ToList();
        var uninstall = UninstallCommand.TryCreate(app);
        if (chosen.Count == 0 && uninstall is null)
        {
            Dialogs.Info("Nothing is ticked, and this program has no uninstaller DiskSweeper can run.");
            return;
        }

        var folders = chosen.Where(row => row.Item.Kind is RemovalKind.InstallFolder or RemovalKind.LeftoverFolder).Select(row => row.Location).ToList();
        var running = await Task.Run(() => RunningPrograms.Blocking(RunningPrograms.Capture(), app, folders, uninstall));
        var confirm = new ConfirmProgramRemovalViewModel(_selected, chosen, uninstall, running, _settings.Method, preview);
        if (new Views.ConfirmProgramRemovalWindow(confirm) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog() != true) return;

        _removeCancel = new CancellationTokenSource();
        var token = _removeCancel.Token;
        IsRemoving = true;
        RemovingChanged?.Invoke(true);
        try
        {
            await ExecuteAsync(app, confirm, token);
            if (!confirm.DryRun) await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            Notify?.Invoke("Stopped.", false);
        }
        finally
        {
            _removeCancel.Dispose();
            _removeCancel = null;
            IsRemoving = false;
            RemovingChanged?.Invoke(false);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ExecuteAsync(CatalogApp app, ConfirmProgramRemovalViewModel confirm, CancellationToken cancellationToken)
    {
        var log = _coordinator.Service.Log;
        var rows = new List<DeleteResultRow>();

        if (!confirm.DryRun && confirm.CloseFirst && confirm.Running.Count > 0)
        {
            Progress?.Invoke("Closing programs that are still running…");
            var lockers = confirm.Running.Select(process => new LockingProcess(
                process.ProcessId, process.StartTimeUtc, process.ProcessName, process.ProcessName, null, LockerKind.App)).ToList();
            var report = await Task.Run(() => AppCloser.Close(lockers, force: false), cancellationToken);
            if (report.StillRunning.Any(process => process.Kind != LockerKind.Critical && !process.IsThisApp))
            {
                string names = string.Join(", ", report.StillRunning.Select(process => process.DisplayName));
                if (Dialogs.Confirm($"{names} didn't close. Force it to close? Anything unsaved in it will be lost."))
                    report = await Task.Run(() => AppCloser.Close(report.StillRunning, force: true), cancellationToken);
            }
            if (report.StillRunning.Any(process => process.Kind != LockerKind.Critical && !process.IsThisApp)
                && !Dialogs.Confirm("Something is still running. Continue anyway? Files it has open may not be removed."))
                return;
        }

        if (confirm.Uninstall is UninstallLaunch launch)
        {
            Progress?.Invoke(confirm.DryRun ? "Previewing the uninstaller…" : "Running the uninstaller…");
            if (confirm.DryRun)
            {
                log?.Write("Uninstall", "WouldRun", 0, launch.DisplayText, "Dry run: the uninstaller was not started.");
                rows.Add(ResultRow("Official uninstaller", launch.DisplayText, DeleteOutcome.WouldDelete, "Dry run: the uninstaller would run."));
            }
            else
            {
                var outcome = await UninstallRunner.RunAsync(launch, cancellationToken);
                log?.Write("Uninstall", outcome.Succeeded ? "Ran" : "Failed", 0, launch.DisplayText, outcome.Message);
                rows.Add(ResultRow("Official uninstaller", launch.DisplayText, outcome.Succeeded ? DeleteOutcome.Deleted : DeleteOutcome.Failed, outcome.Message));
                if (!outcome.Succeeded && !Dialogs.Confirm(outcome.Message + "\n\nRemove the leftover files anyway?"))
                {
                    ShowResults(rows, confirm);
                    return;
                }
                if (outcome.Succeeded && launch.Style == UninstallStyle.LauncherHandoff
                    && !Dialogs.Confirm(outcome.Message + "\n\nWhen that is finished, DiskSweeper can remove leftovers that are still on disk. Continue?"))
                {
                    ShowResults(rows, confirm);
                    return;
                }
            }
        }

        var fileItems = confirm.Chosen.Where(row => row.Item.Kind != RemovalKind.RegistryKey).Select(row => new DeletePlanItem
        {
            Title = row.Title,
            Path = row.Location,
            Paths = [row.Location],
            Size = row.Item.SizeBytes,
            Warnings = PathWarnings.For(row.Location),
            CantRecycle = !RecycleBin.CanTake(row.Location),
        }).ToList();

        if (fileItems.Count > 0)
        {
            var plan = new DeletePlan { Items = fileItems, Method = confirm.Method, DryRun = confirm.DryRun };
            rows.AddRange(await _coordinator.ExecuteAsync(plan, new Progress<string>(message => Progress?.Invoke(message)), cancellationToken));
        }

        var registry = confirm.Chosen.Where(row => row.Item.Kind == RemovalKind.RegistryKey).Select(row => row.Location).ToList();
        if (registry.Count > 0)
        {
            Progress?.Invoke(confirm.DryRun ? "Checking registry keys…" : "Removing registry keys…");
            var local = new List<DeleteResult>();
            var elevated = new List<string>();
            foreach (string key in registry)
            {
                if (confirm.DryRun || !RegistryKeys.NeedsAdmin(key) || Elevation.IsElevated)
                    local.Add(RegistryKeys.Delete(key, confirm.DryRun, log));
                else
                    elevated.Add(key);
            }
            if (elevated.Count > 0)
                local.AddRange(await ElevatedRunner.RunAsync(AdminAction.DeleteRegistryKey, elevated, confirm.Method, _coordinator.Service));

            foreach (var result in local)
            {
                string title = result.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? result.Path;
                rows.Add(new DeleteResultRow(new DeletePlanItem
                {
                    Title = "Registry: " + title,
                    Path = result.Path,
                    Paths = [result.Path],
                    Size = 0,
                }, [result]));
            }
        }

        ShowResults(rows, confirm);
        var summary = new DeleteResultsViewModel(rows, _coordinator, new DeletePlan { Items = fileItems, Method = confirm.Method, DryRun = confirm.DryRun });
        Notify?.Invoke(summary.Summary, rows.Any(row => !row.AllSucceeded));
    }

    private void ShowResults(IReadOnlyList<DeleteResultRow> rows, ConfirmProgramRemovalViewModel confirm)
    {
        if (rows.Count == 0) return;
        var plan = new DeletePlan
        {
            Items = confirm.Chosen.Select(row => new DeletePlanItem { Title = row.Title, Path = row.Location, Paths = [row.Location], Size = row.Item.SizeBytes }).ToList(),
            Method = confirm.Method,
            DryRun = confirm.DryRun,
        };
        new Views.DeleteResultsWindow(new DeleteResultsViewModel(rows, _coordinator, plan)) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
    }

    private static DeleteResultRow ResultRow(string title, string path, DeleteOutcome outcome, string message) =>
        new(new DeletePlanItem { Title = title, Path = path, Paths = [path], Size = 0 },
            [new DeleteResult { Path = path, Outcome = outcome, Message = message, Reason = outcome == DeleteOutcome.Failed ? BlockReason.Other : BlockReason.None }]);
}
