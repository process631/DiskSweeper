using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Scanning;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.App.ViewModels;

public sealed class FindingRowViewModel(Finding finding, SelectionViewModel selection) : ObservableObject
{
    public Finding Finding { get; } = finding;

    public string Title => Finding.Title;

    public string Path => Finding.Path;

    public string Explanation => Finding.Explanation;

    public string SizeText => ByteSize.Format(Finding.Size);

    public long Size => Finding.Size;

    public string Group => Finding.Category switch
    {
        FindCategory.Junk => "1 · Junk and caches",
        FindCategory.GameLeftover => "2 · Game leftovers",
        FindCategory.ProgramLeftover => "3 · Leftovers of uninstalled programs",
        FindCategory.GameSaves => "4 · Saves and settings of games that seem uninstalled",
        FindCategory.Downloads => "5 · Old downloads",
        FindCategory.Developer => "6 · Developer caches",
        _ => "7 · Space Windows manages (use the Windows tool)",
    };

    public string Badge => Finding.Confidence switch
    {
        Confidence.Safe => "Safe",
        Confidence.Review => "Review",
        _ => "Windows",
    };

    public string Tags => string.Join("  ·  ", new[]
    {
        Finding.MayContainSaves ? "may contain game saves" : null,
        Finding.NeedsAdmin && !Elevation.IsElevated ? "some files need administrator" : null,
        Finding.ContentsOnly ? "empties the folder, keeps the folder itself" : null,
        Finding.LastChangedUtc is DateTime changed && changed > DateTime.MinValue.AddYears(1) ? $"last changed {changed.ToLocalTime():yyyy-MM-dd}" : null,
    }.OfType<string>());

    public bool HasTags => Tags.Length > 0;

    public bool CanSelect => Finding.Action != FindingAction.AdviceOnly;

    public bool HasAdvice => Finding.Advice is not null;

    public string? Advice => Finding.Advice;

    public bool HasCommand => Finding.AdviceCommand is not null;

    public string CommandText => Finding.AdviceCommand ?? "";

    public bool IsChecked
    {
        get => selection.Contains("finding:" + Finding.Id);
        set => selection.Set(SelectedItem.From(Finding), value);
    }

    public void RefreshChecked() => Raise(nameof(IsChecked));
}

/// <summary>The Smart finds tab: junk, leftovers and advice, grouped by category.</summary>
public sealed class SmartFindsViewModel : ObservableObject
{
    private readonly SelectionViewModel _selection;
    private string _status = "Smart finds looks for caches, leftovers of uninstalled games and programs, and old downloads. Nothing is deleted until you tick it and press Delete.";
    private bool _isBusy;
    private CancellationTokenSource? _cancel;

    public SmartFindsViewModel(SelectionViewModel selection)
    {
        _selection = selection;
        selection.Changed += (_, _) =>
        {
            foreach (var row in Rows) row.RefreshChecked();
        };
        View = CollectionViewSource.GetDefaultView(Rows);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FindingRowViewModel.Group)));
        View.SortDescriptions.Add(new SortDescription(nameof(FindingRowViewModel.Group), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(FindingRowViewModel.Size), ListSortDirection.Descending));

        SelectSafeCommand = new RelayCommand(SelectSafe, () => Rows.Any(r => r.Finding.Confidence == Confidence.Safe && r.CanSelect));
        OpenBcuCommand = new RelayCommand(() => BulkCrapUninstaller.Open());
        RunAdviceCommand = new RelayCommand(RunAdvice);
        CopyCommand = new RelayCommand(p => Shell.CopyText(p as string ?? ""));
    }

    public BulkObservableCollection<FindingRowViewModel> Rows { get; } = [];

    public ICollectionView View { get; }

    public ICommand SelectSafeCommand { get; }

    public ICommand OpenBcuCommand { get; }

    public ICommand RunAdviceCommand { get; }

    public ICommand CopyCommand { get; }

    public string BcuButtonText { get; } = BulkCrapUninstaller.FindExecutable() is null
        ? "Get Bulk Crap Uninstaller (free)…"
        : "Open Bulk Crap Uninstaller";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public async Task FindAsync(ScanResult? scan)
    {
        _cancel?.Cancel();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => Status = message);
            var findings = await new SmartFinder(scan).FindAsync(progress, token);
            Rows.ReplaceAll(findings.Select(f => new FindingRowViewModel(f, _selection)));

            long safe = findings.Where(f => f.Confidence == Confidence.Safe).Sum(f => f.Size);
            long review = findings.Where(f => f.Confidence == Confidence.Review).Sum(f => f.Size);
            Status = findings.Count == 0
                ? "Nothing worth cleaning was found. Nice and tidy!"
                : $"Found {ByteSize.Format(safe)} that's safe to clean and {ByteSize.Format(review)} worth a look. Tick what you want gone, then press Delete.";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Drops findings that were cleaned so the list stays honest.</summary>
    public void RemoveCleaned(IEnumerable<Finding> cleaned)
    {
        var gone = cleaned.ToHashSet();
        if (gone.Count == 0) return;
        Rows.ReplaceAll(Rows.Where(r => !gone.Contains(r.Finding)).ToList());
    }

    private void SelectSafe()
    {
        foreach (var row in Rows.Where(r => r.Finding.Confidence == Confidence.Safe && r.CanSelect)) row.IsChecked = true;
    }

    private void RunAdvice(object? parameter)
    {
        if (parameter is not string command) return;
        if (Dialogs.Confirm($"Run this?\n\n{command}\n\nCommands that change Windows settings open in an administrator PowerShell window so you can see what happens."))
            Shell.RunAdvice(command);
    }
}
