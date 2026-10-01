using System.Windows.Input;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.App.ViewModels;

/// <summary>One line of the "are you sure?" list.</summary>
public sealed class DeletePlanItem
{
    public required string Title { get; init; }

    public required string Path { get; init; }

    /// <summary>What actually gets deleted: the item itself, or for "empty this cache" finds, the items inside.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    public long Size { get; init; }

    public string SizeText => ByteSize.Format(Size);

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    public string? Refusal { get; init; }

    public bool IsRefused => Refusal is not null;

    public FileNode? Node { get; init; }

    public Finding? Finding { get; init; }

    public bool EmptiesRecycleBin { get; init; }

    /// <summary>The Recycle Bin can't take this (path too long or a name ending in a dot or space).</summary>
    public bool CantRecycle { get; init; }

    public string Glyph => Finding is not null ? Icons.Broom : Node?.IsDirectory == true ? Icons.Folder : Icons.File;
}

public sealed class DeletePlan
{
    public required IReadOnlyList<DeletePlanItem> Items { get; init; }

    public required DeleteMethod Method { get; init; }

    public bool DryRun { get; init; }

    public IReadOnlyList<DeletePlanItem> ToDelete => Items.Where(i => !i.IsRefused).ToList();

    public IReadOnlyList<DeletePlanItem> Refused => Items.Where(i => i.IsRefused).ToList();

    public bool HasRefused => Refused.Count > 0;

    public long TotalSize => ToDelete.Sum(i => i.Size);
}

/// <summary>
/// The confirmation dialog: shows exactly what will go, lets you choose Recycle Bin or permanent (and a dry
/// run), and asks for one extra tick before big or permanent deletes.
/// </summary>
public sealed class ConfirmDeleteViewModel(DeletePlan plan) : ObservableObject
{
    private DeleteMethod _method = plan.Method;
    private bool _dryRun = plan.DryRun;
    private bool _extraConfirmed;

    public IReadOnlyList<DeletePlanItem> Items { get; } = plan.ToDelete;

    public IReadOnlyList<DeletePlanItem> Refused { get; } = plan.Refused;

    public bool HasRefused => Refused.Count > 0;

    public long TotalSize => Items.Sum(i => i.Size);

    public string TotalText => ByteSize.Format(TotalSize);

    public string CountText => $"{Items.Count:N0} item{(Items.Count == 1 ? "" : "s")}";

    public string Title => _dryRun ? "Preview this cleanup" : "Delete the selected items?";

    public bool UseRecycleBin
    {
        get => _method == DeleteMethod.RecycleBin;
        set
        {
            if (value) SetMethod(DeleteMethod.RecycleBin);
        }
    }

    public bool UsePermanent
    {
        get => _method == DeleteMethod.Permanent;
        set
        {
            if (value) SetMethod(DeleteMethod.Permanent);
        }
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
        ? $"I understand that {TotalText} will be deleted permanently and can't be restored."
        : $"Yes, move {TotalText} to the Recycle Bin.";

    public bool ExtraConfirmed
    {
        get => _extraConfirmed;
        set
        {
            if (Set(ref _extraConfirmed, value)) Raise(nameof(CanConfirm));
        }
    }

    public bool CanConfirm => !NeedsExtraConfirmation || _extraConfirmed;

    public string ConfirmText => _dryRun ? "Run preview" : _method == DeleteMethod.RecycleBin ? "Move to Recycle Bin" : "Delete permanently";

    public bool ShowCantRecycleWarning => !_dryRun && _method == DeleteMethod.RecycleBin && Items.Any(i => i.CantRecycle);

    public string CantRecycleText =>
        $"{Items.Count(i => i.CantRecycle):N0} item(s) have paths the Recycle Bin can't hold. They'll be listed afterwards with a button to delete them permanently.";

    public DeletePlan ToPlan() => new() { Items = plan.Items, Method = _method, DryRun = _dryRun };

    private void SetMethod(DeleteMethod method)
    {
        if (_method == method) return;
        _method = method;
        _extraConfirmed = false;
        RaiseAll();
    }
}

/// <summary>One line of the results window, with the fixes that apply to whatever went wrong.</summary>
public sealed class DeleteResultRow : ObservableObject
{
    private List<DeleteResult> _results;
    private string? _note;

    public DeleteResultRow(DeletePlanItem item, IEnumerable<DeleteResult> results)
    {
        Item = item;
        _results = results.ToList();
    }

    public DeletePlanItem Item { get; }

    public IReadOnlyList<DeleteResult> Results => _results;

    public string Title => Item.Title;

    public string Path => Item.Path;

    public bool AllSucceeded => _results.All(r => r.Succeeded);

    public long Bytes => _results.Sum(r => r.Bytes);

    public string Icon => _results.Count == 0 ? Icons.Info
        : AllSucceeded ? (_results.Any(r => r.Outcome == DeleteOutcome.ScheduledForRestart) ? Icons.Clock : Icons.Check)
        : _results.Any(r => r.Succeeded || r.Bytes > 0) ? Icons.Warning : Icons.Cancel;

    public string StatusText
    {
        get
        {
            if (_results.Count == 0) return "Nothing to delete (it was already empty).";
            var outcomes = _results.Select(r => r.Outcome).Distinct().ToList();
            if (AllSucceeded)
            {
                return outcomes.Contains(DeleteOutcome.WouldDelete) ? $"Would free {ByteSize.Format(Bytes)}."
                    : outcomes.Contains(DeleteOutcome.ScheduledForRestart) ? "Will be deleted when Windows restarts."
                    : outcomes.Contains(DeleteOutcome.Recycled) ? $"Moved {ByteSize.Format(Bytes)} to the Recycle Bin."
                    : $"Deleted, freed {ByteSize.Format(Bytes)}.";
            }
            int failed = _results.Count(r => !r.Succeeded);
            return failed == _results.Count && Bytes == 0
                ? "Couldn't be deleted."
                : $"Partly done: {ByteSize.Format(Bytes)} handled, {failed:N0} item(s) refused.";
        }
    }

    public string Message => string.Join("\n", _results.Where(r => !r.Succeeded).Select(r => r.Message).OfType<string>().Distinct().Take(3)
        .Append(_note).OfType<string>());

    public bool HasMessage => Message.Length > 0;

    public IReadOnlyList<LockingProcess> Lockers => _results.SelectMany(r => r.LockedBy).DistinctBy(l => l.ProcessId)
        .Where(l => l.Kind != LockerKind.Critical && !l.IsThisApp).ToList();

    public IReadOnlyList<string> FailedPaths => _results.Where(r => !r.Succeeded).Select(r => r.Path).ToList();

    public bool CanCloseApps => Lockers.Count > 0;

    public string CloseAppsText => $"Close {string.Join(", ", Lockers.Select(l => l.DisplayName).Distinct().Take(2))} and retry";

    public bool CanRestartDelete => _results.Any(r => !r.Succeeded && r.Reason is BlockReason.InUse or BlockReason.AccessDenied or BlockReason.Other);

    public bool CanTakeOwnership => _results.Any(r => !r.Succeeded && r.Reason == BlockReason.AccessDenied);

    public bool CanDeletePermanently => _results.Any(r => !r.Succeeded && r.Reason is BlockReason.RecycleBinCantTakeIt or BlockReason.RecycleBinFailed);

    public bool NeedsAdmin => !Elevation.IsElevated;

    /// <summary>Replaces the results for the paths in <paramref name="updated"/> (after a fix) and refreshes the line.</summary>
    public void Update(IEnumerable<DeleteResult> updated, string? note = null)
    {
        var byPath = updated.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
        _results = _results.Select(r => byPath.TryGetValue(r.Path, out var fresh) ? fresh : r).ToList();
        _note = note;
        RaiseAll();
    }
}

/// <summary>Turns ticked items into a delete plan, runs it, and runs the fixes offered in the results window.</summary>
public sealed class DeleteCoordinator(DeleteService service)
{
    public DeleteService Service { get; } = service;

    public DeletePlan Plan(IEnumerable<SelectedItem> items, DeleteMethod method, bool dryRun)
    {
        var planItems = new List<DeletePlanItem>();
        foreach (var item in items)
        {
            if (item.Finding is Finding finding)
            {
                planItems.Add(PlanFinding(finding));
                continue;
            }

            planItems.Add(new DeletePlanItem
            {
                Title = item.Title,
                Path = item.Path,
                Paths = [item.Path],
                Size = item.Size,
                Warnings = PathWarnings.For(item.Path),
                Refusal = ProtectedPaths.WhyProtected(item.Path),
                Node = item.Node,
                CantRecycle = !RecycleBin.CanTake(item.Path),
            });
        }
        return new DeletePlan { Items = planItems.OrderByDescending(i => i.Size).ToList(), Method = method, DryRun = dryRun };
    }

    private static DeletePlanItem PlanFinding(Finding finding)
    {
        if (finding.Action == FindingAction.EmptyRecycleBin)
        {
            return new DeletePlanItem
            {
                Title = finding.Title, Path = "Recycle Bin", Paths = [], Size = finding.Size, Finding = finding, EmptiesRecycleBin = true,
                Warnings = ["Emptying the Recycle Bin can't be undone."],
            };
        }

        var paths = finding.ItemsToDelete().Where(p => ProtectedPaths.WhyProtected(p) is null).ToList();
        var warnings = new List<string>();
        if (finding.Confidence == Confidence.Review) warnings.Add("Marked Review: " + finding.Explanation);
        if (finding.MayContainSaves) warnings.Add("May contain game saves.");
        bool appFolder = finding.Category is FindCategory.ProgramLeftover or FindCategory.GameSaves;
        warnings.AddRange(PathWarnings.For(finding.Path).Where(w => !w.Contains("game saves") && (appFolder || !w.Contains("settings folder"))));

        return new DeletePlanItem
        {
            Title = finding.Title,
            Path = finding.Path,
            Paths = paths,
            Size = finding.Size,
            Warnings = warnings.Distinct().ToList(),
            Refusal = finding.Action == FindingAction.AdviceOnly ? "Windows manages this; use the advice shown in Cleanup." : null,
            Finding = finding,
            CantRecycle = paths.Any(path => !RecycleBin.CanTake(path)),
        };
    }

    public async Task<List<DeleteResultRow>> ExecuteAsync(DeletePlan plan, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var rows = new List<DeleteResultRow>();
        var items = plan.ToDelete;
        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            progress.Report($"{(plan.DryRun ? "Checking" : "Deleting")} {i + 1} of {items.Count}: {item.Title}");

            if (item.EmptiesRecycleBin)
            {
                bool emptied = plan.DryRun || await Task.Run(RecycleBin.Empty, cancellationToken);
                rows.Add(new DeleteResultRow(item,
                [
                    new DeleteResult
                    {
                        Path = "Recycle Bin", Bytes = item.Size,
                        Outcome = plan.DryRun ? DeleteOutcome.WouldDelete : emptied ? DeleteOutcome.Deleted : DeleteOutcome.Failed,
                        Message = emptied ? null : "Windows didn't empty the Recycle Bin (it may already be empty).",
                    },
                ]));
                continue;
            }

            long? knownSize = item.Paths.Count == 1 && item.Node is not null ? item.Size : null;
            var results = await Task.Run(() => item.Paths
                .Select(path => Service.Delete(path, plan.Method, plan.DryRun, knownSize, cancellationToken))
                .ToList(), cancellationToken);
            rows.Add(new DeleteResultRow(item, results));
        }
        return rows;
    }

    public async Task CloseAppsAndRetryAsync(DeleteResultRow row, DeleteMethod method)
    {
        var report = await Task.Run(() => AppCloser.Close(row.Lockers, force: false));
        if (report.StillRunning.Any(l => l.Kind != LockerKind.Critical && !l.IsThisApp))
        {
            string names = string.Join(", ", report.StillRunning.Select(l => l.DisplayName));
            if (Dialogs.Confirm($"{names} didn't close. Force it to close? Anything unsaved in it will be lost."))
                report = await Task.Run(() => AppCloser.Close(report.StillRunning, force: true));
        }

        var retried = await Task.Run(() => row.FailedPaths.Select(path => Service.Delete(path, method)).ToList());
        row.Update(retried, string.Join("\n", report.Notes));
    }

    public async Task DeleteOnRestartAsync(DeleteResultRow row) =>
        row.Update(await ElevatedRunner.RunAsync(AdminAction.DeleteOnRestart, row.FailedPaths, DeleteMethod.Permanent, Service));

    public async Task TakeOwnershipAsync(DeleteResultRow row, DeleteMethod method) =>
        row.Update(await ElevatedRunner.RunAsync(AdminAction.TakeOwnershipAndDelete, row.FailedPaths, method, Service));

    public async Task DeletePermanentlyAsync(DeleteResultRow row) =>
        row.Update(await Task.Run(() => row.FailedPaths.Select(path => Service.Delete(path, DeleteMethod.Permanent)).ToList()));
}

/// <summary>The results window: what happened to each item and buttons for the fixes that apply.</summary>
public sealed class DeleteResultsViewModel : ObservableObject
{
    private readonly DeleteCoordinator _coordinator;
    private readonly DeleteMethod _method;
    private bool _isBusy;

    public DeleteResultsViewModel(IReadOnlyList<DeleteResultRow> rows, DeleteCoordinator coordinator, DeletePlan plan)
    {
        Rows = rows;
        _coordinator = coordinator;
        _method = plan.Method;
        DryRun = plan.DryRun;

        CloseAppsCommand = Fix(row => _coordinator.CloseAppsAndRetryAsync(row, _method));
        RestartDeleteCommand = Fix(row => _coordinator.DeleteOnRestartAsync(row));
        TakeOwnershipCommand = Fix(row => _coordinator.TakeOwnershipAsync(row, _method));
        DeletePermanentlyCommand = Fix(row => _coordinator.DeletePermanentlyAsync(row));
        OpenLocationCommand = new RelayCommand(p => { if (p is DeleteResultRow row) Shell.ShowInExplorer(row.Path); });
    }

    public IReadOnlyList<DeleteResultRow> Rows { get; }

    public bool DryRun { get; }

    public ICommand CloseAppsCommand { get; }

    public ICommand RestartDeleteCommand { get; }

    public ICommand TakeOwnershipCommand { get; }

    public ICommand DeletePermanentlyCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool AllGood => Rows.All(r => r.AllSucceeded);

    public string HeadlineGlyph => AllGood ? Icons.Check : Icons.Warning;

    public string Headline
    {
        get
        {
            long bytes = Rows.Sum(r => r.Bytes);
            int failed = Rows.Count(r => !r.AllSucceeded);
            if (DryRun) return $"Preview: {ByteSize.Format(bytes)} would be freed";
            if (failed > 0) return failed == 1 ? "1 item needs your attention" : $"{failed} items need your attention";
            return _method == DeleteMethod.RecycleBin ? $"Moved {ByteSize.Format(bytes)} to the Recycle Bin" : $"Freed {ByteSize.Format(bytes)}";
        }
    }

    /// <summary>The line under the headline in the results window.</summary>
    public string Detail => DryRun
        ? "Nothing was deleted. The preview is in the History page."
        : Rows.All(r => r.AllSucceeded)
            ? (_method == DeleteMethod.RecycleBin ? "Recycled items still use space until you empty the Recycle Bin." : "Everything selected was removed.")
            : "Use the buttons next to each item to fix what couldn't be deleted.";

    public string Summary
    {
        get
        {
            long bytes = Rows.Sum(r => r.Bytes);
            int failed = Rows.Count(r => !r.AllSucceeded);
            string done = DryRun ? $"Dry run: {ByteSize.Format(bytes)} would be freed."
                : _method == DeleteMethod.RecycleBin ? $"{ByteSize.Format(bytes)} handled. Recycled items still use space until you empty the Recycle Bin."
                : $"{ByteSize.Format(bytes)} freed.";
            return failed == 0 ? done : $"{done} {failed} item(s) need attention: use the buttons next to them.";
        }
    }

    private AsyncCommand Fix(Func<DeleteResultRow, Task> fix) => new(async parameter =>
    {
        if (parameter is not DeleteResultRow row) return;
        IsBusy = true;
        try
        {
            await fix(row);
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(Summary));
            Raise(nameof(Headline));
            Raise(nameof(Detail));
            Raise(nameof(HeadlineGlyph));
            Raise(nameof(AllGood));
        }
    });
}
