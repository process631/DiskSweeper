using DiskSweeper.Core;
using DiskSweeper.Core.Analysis;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Cli;

internal static class ScanCommand
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var progress = new ScanProgress();
        string path;
        Task<ScanResult> scan;
        try
        {
            path = LongPath.Normalize(options.Path);
            if (options.Fast && !MftScanner.CanScan(path, out string reason))
            {
                ConsoleOut.WriteLine($"{reason} Using the normal folder walk instead.", ConsoleColor.Yellow);
                options = options.WithoutFast();
            }

            scan = options.Fast
                ? new MftScanner().ScanAsync(path, progress, cancellationToken)
                : new DiskScanner(options.Threads is int threads ? new ScanOptions { MaxParallelism = threads } : null).ScanAsync(path, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException or NotSupportedException)
        {
            ConsoleOut.WriteLine(ex.Message, ConsoleColor.Red);
            return 1;
        }

        var result = await ShowProgressAsync(scan, progress, path);

        // Look up the previous scan before saving this one, or we'd compare the scan with itself.
        var previous = options.Compare ? ScanStore.List(result.Root.Name).FirstOrDefault() : null;
        new ScanReport(result, options).Print();

        if (options.Compare) PrintChanges(previous, result);
        if (options.Save)
        {
            string file = ScanStore.Save(result);
            ConsoleOut.WriteLine();
            ConsoleOut.WriteLine($"Saved this scan to {file}", ConsoleColor.DarkGray);
        }
        return 0;
    }

    private static void PrintChanges(SavedScanInfo? previous, ScanResult result)
    {
        if (previous is null)
        {
            ConsoleOut.Heading("Changes");
            ConsoleOut.WriteLine($"There's no earlier saved scan of {result.Root.Name}. Add --save to keep this one for next time.", ConsoleColor.Yellow);
            return;
        }

        var before = ScanStore.Load(previous.File);
        var changes = ScanComparer.Compare(before.Root, result.Root, minimumChange: 50L << 20, maxResults: 30);
        ConsoleOut.Heading($"Changes since {previous.ScannedAtUtc.ToLocalTime():g} ({ByteSize.FormatDelta(result.Root.Size - before.Root.Size)} overall)");
        if (changes.Count == 0)
        {
            ConsoleOut.WriteLine("Nothing grew or shrank by 50 MB or more.");
            return;
        }

        int pathWidth = Math.Max(20, ConsoleOut.Width - 34);
        foreach (var change in changes)
        {
            var color = change.Delta > 0 ? ConsoleColor.Red : ConsoleColor.Green;
            ConsoleOut.Write(ByteSize.FormatDelta(change.Delta).PadLeft(11), color);
            ConsoleOut.Write($"  {change.Kind,-7} ", ConsoleColor.Gray);
            ConsoleOut.WriteLine(ConsoleOut.ShortenMiddle(change.Path + (change.IsDirectory ? "\\" : ""), pathWidth));
        }
    }

    /// <summary>Redraws a one-line progress counter until the scan finishes, then clears it.</summary>
    internal static async Task<T> ShowProgressAsync<T>(Task<T> work, ScanProgress progress, string label)
    {
        if (Console.IsOutputRedirected) return await work;

        int lastLength = 0;
        while (!work.IsCompleted)
        {
            string line = progress.Fraction is double fraction
                ? $"Reading the file table of {label} … {fraction:P0}"
                : $"Scanning {label} … {progress.FilesScanned:N0} files · {progress.FoldersScanned:N0} folders · {ByteSize.Format(progress.BytesScanned)}";
            line = ConsoleOut.Truncate(line, ConsoleOut.Width - 1);
            Console.Write("\r" + line.PadRight(lastLength));
            lastLength = line.Length;
            await Task.WhenAny(work, Task.Delay(200));
        }
        Console.Write("\r" + new string(' ', lastLength) + "\r");
        return await work;
    }
}
