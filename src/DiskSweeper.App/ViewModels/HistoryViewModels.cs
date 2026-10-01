using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.Core;
using DiskSweeper.Core.Analysis;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App.ViewModels;

public sealed record ChangeRow(string Path, string Change, long Delta, string Kind, string Before, string After);

public sealed record SavedScanChoice(SavedScanInfo Info)
{
    public override string ToString() =>
        $"{Info.ScannedAtUtc.ToLocalTime():g}  ·  {ByteSize.Format(Info.TotalSize)}  ·  {Info.FileCount:N0} files";
}

/// <summary>The Changes tab: what grew or shrank compared with an earlier saved scan.</summary>
public sealed class ChangesViewModel : ObservableObject
{
    private ScanResult? _current;
    private SavedScanChoice? _selected;
    private string _summary = "Scan a drive twice (scans are saved automatically) to see what changed in between.";

    public ChangesViewModel()
    {
        CompareCommand = new AsyncCommand(CompareAsync, () => _current is not null && Selected is not null);
    }

    public BulkObservableCollection<SavedScanChoice> SavedScans { get; } = [];

    public BulkObservableCollection<ChangeRow> Rows { get; } = [];

    public ICommand CompareCommand { get; }

    public SavedScanChoice? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>Lists earlier scans of the same folder and compares with the newest one automatically.</summary>
    public async Task LoadAsync(ScanResult current)
    {
        _current = current;
        var earlier = ScanStore.List(current.Root.Name)
            .Where(s => s.ScannedAtUtc < current.ScannedAtUtc.AddSeconds(-1))
            .Select(s => new SavedScanChoice(s))
            .ToList();
        SavedScans.ReplaceAll(earlier);
        Selected = earlier.FirstOrDefault();
        Rows.ReplaceAll([]);

        if (Selected is null)
            Summary = $"No earlier saved scan of {current.Root.Name} yet. Next time you scan, this tab shows what changed.";
        else
            await CompareAsync();
    }

    private async Task CompareAsync()
    {
        if (_current is null || Selected is null) return;

        Summary = "Comparing…";
        var current = _current;
        var info = Selected.Info;
        var changes = await Task.Run(() => ScanComparer.Compare(ScanStore.Load(info.File).Root, current.Root, minimumChange: 50L << 20, maxResults: 300));

        Rows.ReplaceAll(changes.Select(c => new ChangeRow(
            c.Path + (c.IsDirectory ? "\\" : ""),
            ByteSize.FormatDelta(c.Delta),
            c.Delta,
            c.Kind.ToString(),
            c.Kind == ChangeKind.New ? "" : ByteSize.Format(c.OldSize),
            c.Kind == ChangeKind.Gone ? "" : ByteSize.Format(c.NewSize))));

        long overall = current.Root.Size - info.TotalSize;
        Summary = changes.Count == 0
            ? $"Since {info.ScannedAtUtc.ToLocalTime():g}: {ByteSize.FormatDelta(overall)} overall, nothing changed by 50 MB or more."
            : $"Since {info.ScannedAtUtc.ToLocalTime():g}: {ByteSize.FormatDelta(overall)} overall. These places changed the most:";
    }
}

public sealed record LogRow(string Time, string Action, string Outcome, string Size, string Path, string Details);

/// <summary>The Log tab: every delete attempt DiskSweeper made, newest first.</summary>
public sealed class LogViewModel : ObservableObject
{
    private readonly DeletionLog _log;

    public LogViewModel(DeletionLog log)
    {
        _log = log;
        RefreshCommand = new RelayCommand(Refresh);
        OpenFileCommand = new RelayCommand(() =>
        {
            if (File.Exists(_log.FilePath)) Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
        });
    }

    public BulkObservableCollection<LogRow> Rows { get; } = [];

    public string FilePath => _log.FilePath;

    public ICommand RefreshCommand { get; }

    public ICommand OpenFileCommand { get; }

    public void Refresh()
    {
        if (!File.Exists(_log.FilePath))
        {
            Rows.ReplaceAll([]);
            return;
        }

        string[] lines;
        try
        {
            using var stream = new FileStream(_log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return;
        }

        Rows.ReplaceAll(lines.Skip(1).Reverse().Take(2000).Select(ParseCsv).Where(f => f.Count >= 7)
            .Select(f => new LogRow(f[0], f[1], f[2], f[4], f[5], f[6])));
    }

    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (c != '\r')
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
