using DiskSweeper.Core;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Cli;

/// <summary>Prints a scan as a size-sorted tree, a list of the biggest files, and notes about anything left out.</summary>
internal sealed class ScanReport(ScanResult result, CliOptions options)
{
    private const int SizeWidth = 10;
    private const int PercentWidth = 6;
    private const int BarWidth = 20;

    private readonly int _nameWidth = Math.Clamp(ConsoleOut.Width - (SizeWidth + PercentWidth + BarWidth + 5), 30, 100);
    private readonly double _total = Math.Max(1, result.Root.Size);
    private readonly long _expandThreshold = (long)(result.Root.Size * options.ExpandPercent / 100);

    public void Print()
    {
        PrintSummary();
        PrintTree();
        PrintLargestFiles();
        PrintNotes();
    }

    private void PrintSummary()
    {
        var root = result.Root;
        string method = result.Method == ScanMethod.MasterFileTable ? "fast NTFS read" : "folder walk";
        Console.WriteLine();
        ConsoleOut.Write("Scanned ");
        ConsoleOut.Write(root.Name, ConsoleColor.White);
        ConsoleOut.WriteLine($" in {result.Duration.TotalSeconds:0.0} s ({method})");
        ConsoleOut.Write("  ");
        ConsoleOut.Write(ByteSize.Format(root.Size), ConsoleColor.Yellow);
        ConsoleOut.WriteLine($" in {root.FileCount:N0} files and {root.FolderCount:N0} folders");
    }

    private void PrintTree()
    {
        ConsoleOut.Heading($"Biggest items (top {options.Top} per folder, opened {options.Depth} levels deep)");
        PrintRow("", result.Root, isRoot: true);
        if (options.Depth > 0) PrintChildren(result.Root, "", level: 1);
    }

    private void PrintChildren(FileNode folder, string indent, int level)
    {
        var children = folder.Children;
        int shown = Math.Min(children.Count, options.Top);
        int hiddenCount = children.Count - shown;
        long hiddenBytes = 0;
        for (int i = shown; i < children.Count; i++) hiddenBytes += children[i].Size;

        for (int i = 0; i < shown; i++)
        {
            var child = children[i];
            bool last = i == shown - 1 && hiddenCount == 0;
            PrintRow(indent + (last ? "└─ " : "├─ "), child);

            bool expand = child.IsDirectory && level < options.Depth && child.Children.Count > 0
                && child.Size > 0 && child.Size >= _expandThreshold;
            if (expand) PrintChildren(child, indent + (last ? "   " : "│  "), level + 1);
        }

        if (hiddenCount > 0)
        {
            string prefix = indent + "└─ ";
            string label = $"… {hiddenCount:N0} more item{(hiddenCount == 1 ? "" : "s")}";
            ConsoleOut.Write(prefix, ConsoleColor.DarkGray);
            ConsoleOut.Write(label, ConsoleColor.DarkGray);
            WriteNumbers(prefix.Length + label.Length, hiddenBytes);
        }
    }

    private void PrintRow(string prefix, FileNode node, bool isRoot = false)
    {
        string tag = node.IsLink ? " [link, not followed]"
            : node.IsUnreadable ? " [no access]"
            : node.IsCloudOnly ? " [online-only]"
            : "";
        string label = node.IsDirectory && !isRoot ? node.Name + "\\" : node.Name;
        label = ConsoleOut.Truncate(label, Math.Max(_nameWidth - prefix.Length - tag.Length, 8));

        ConsoleOut.Write(prefix, ConsoleColor.DarkGray);
        ConsoleOut.Write(label, node.IsDirectory ? ConsoleColor.White : null);
        ConsoleOut.Write(tag, ConsoleColor.DarkYellow);
        WriteNumbers(prefix.Length + label.Length + tag.Length, node.Size);
    }

    /// <summary>Pads out the name column, then writes size, percent of the whole scan, and a bar.</summary>
    private void WriteNumbers(int usedWidth, long size)
    {
        if (usedWidth < _nameWidth) Console.Write(new string(' ', _nameWidth - usedWidth));
        double fraction = size / _total;
        ConsoleOut.Write(" " + ByteSize.Format(size).PadLeft(SizeWidth), ConsoleOut.SizeColor(size));
        ConsoleOut.Write(" " + $"{fraction * 100:0.0}%".PadLeft(PercentWidth) + "  ", ConsoleColor.Gray);
        ConsoleOut.WriteLine(ConsoleOut.Bar(fraction, BarWidth), ConsoleColor.DarkCyan);
    }

    private void PrintLargestFiles()
    {
        if (options.Files == 0) return;

        var files = result.GetLargestFiles(options.Files);
        if (files.Count == 0) return;

        ConsoleOut.Heading($"{files.Count} biggest files");
        ConsoleOut.WriteLine("   #        Size  Modified    Path", ConsoleColor.Gray);

        int pathWidth = Math.Max(20, ConsoleOut.Width - 31);
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            string path = Console.IsOutputRedirected ? file.FullPath : ConsoleOut.ShortenMiddle(file.FullPath, pathWidth);
            ConsoleOut.Write($"{i + 1,4}  ");
            ConsoleOut.Write(ByteSize.Format(file.Size).PadLeft(SizeWidth), ConsoleOut.SizeColor(file.Size));
            ConsoleOut.Write($"  {file.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd}  ", ConsoleColor.Gray);
            ConsoleOut.WriteLine(path);
        }
    }

    private void PrintNotes()
    {
        var notes = new List<(string Text, ConsoleColor Color)>();

        if (result.UnreadableFolders.Count > 0)
        {
            string fix = Elevation.IsElevated
                ? "Some system folders block even administrators, so this is normal."
                : "Run dsweep from an administrator terminal to include them.";
            notes.Add(($"! {result.UnreadableFolders.Count:N0} folders couldn't be read, so their contents aren't counted. {fix}", ConsoleColor.Yellow));
            foreach (string folder in result.UnreadableFolders.Take(5)) notes.Add(($"    {folder}", ConsoleColor.DarkGray));
            if (result.UnreadableFolders.Count > 5) notes.Add(($"    … and {result.UnreadableFolders.Count - 5:N0} more", ConsoleColor.DarkGray));
        }

        if (result.LinkCount > 0)
        {
            notes.Add(($"· {result.LinkCount:N0} links/junctions were skipped. They point at folders that are counted where they really live.", ConsoleColor.Gray));
        }

        if (result.CloudOnlyFileCount > 0)
        {
            notes.Add(($"· {ByteSize.Format(result.CloudOnlyBytes)} in {result.CloudOnlyFileCount:N0} online-only cloud files isn't counted. Those files use no space on this PC.", ConsoleColor.Gray));
        }

        string? driveNote = DriveComparison();
        if (driveNote is not null) notes.Add((driveNote, ConsoleColor.Gray));

        if (notes.Count == 0) return;
        ConsoleOut.Heading("Notes");
        foreach (var (text, color) in notes) ConsoleOut.WriteLine(text, color);
    }

    /// <summary>For a whole-drive scan, compares the total with the used space Windows reports, and explains any gap.</summary>
    private string? DriveComparison()
    {
        string rootPath = result.Root.Name;
        if (!string.Equals(Path.GetPathRoot(rootPath), rootPath, StringComparison.OrdinalIgnoreCase)) return null;

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(rootPath);
            if (!drive.IsReady) return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        long used = drive.TotalSize - drive.TotalFreeSpace;
        long gap = used - result.Root.Size;
        string drv = rootPath.TrimEnd('\\');

        if (Math.Abs(gap) <= used / 100)
            return $"· The scan total matches the {ByteSize.Format(used)} Windows reports as used on {drv}.";
        if (gap > 0)
        {
            var reasons = new List<string>();
            if (result.UnreadableFolders.Count > 0) reasons.Add("folders that couldn't be read");
            if (result.FindNode(Path.Join(rootPath, "System Volume Information")) is not { Size: > 0 }) reasons.Add("System Restore points");
            reasons.Add("the file system's own bookkeeping (such as its Master File Table)");
            return $"· Windows reports {ByteSize.Format(used)} used on {drv}; the scan found {ByteSize.Format(result.Root.Size)}. "
                + $"The other {ByteSize.Format(gap)} is in {string.Join(", ", reasons[..^1])}{(reasons.Count > 1 ? " and " : "")}{reasons[^1]}.";
        }
        return $"· The scan found {ByteSize.Format(-gap)} more than the {ByteSize.Format(used)} Windows reports as used on {drv}. "
            + @"Some files are hard links (stored once but listed in several folders), mostly inside C:\Windows\WinSxS.";
    }
}
