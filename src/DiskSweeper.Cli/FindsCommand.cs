using DiskSweeper.Core;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.Cli;

/// <summary>Lists Smart finds grouped by category. Read-only: deleting happens in the app.</summary>
internal static class FindsCommand
{
    private static readonly (FindCategory Category, string Heading)[] Sections =
    [
        (FindCategory.Junk, "Junk and caches"),
        (FindCategory.GameLeftover, "Game leftovers"),
        (FindCategory.ProgramLeftover, "Leftovers of uninstalled programs"),
        (FindCategory.GameSaves, "Saves and settings of games that seem uninstalled"),
        (FindCategory.Downloads, "Old downloads"),
        (FindCategory.Developer, "Developer caches"),
        (FindCategory.WindowsManaged, "Space Windows manages (use the Windows tool shown)"),
    ];

    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var finder = new SmartFinder(options: new SmartFinderOptions { MinimumSize = options.All ? 1 : 10L << 20 });
        var status = new Progress<string>(message =>
        {
            if (!Console.IsOutputRedirected) Console.Write("\r" + ConsoleOut.Truncate(message, ConsoleOut.Width - 1).PadRight(ConsoleOut.Width - 1));
        });

        var findings = await finder.FindAsync(status, cancellationToken);
        if (!Console.IsOutputRedirected) Console.Write("\r" + new string(' ', ConsoleOut.Width - 1) + "\r");

        int width = ConsoleOut.Width;
        foreach (var (category, heading) in Sections)
        {
            var items = findings.Where(f => f.Category == category).ToList();
            if (items.Count == 0) continue;

            ConsoleOut.Heading($"{heading} — {ByteSize.Format(items.Sum(f => f.Size))}");
            foreach (var finding in items)
            {
                ConsoleOut.Write(ByteSize.Format(finding.Size).PadLeft(10), ConsoleOut.SizeColor(finding.Size));
                ConsoleOut.Write($"  {Label(finding.Confidence),-8}", ColorOf(finding.Confidence));
                ConsoleOut.Write(finding.Title, ConsoleColor.White);
                if (finding.MayContainSaves) ConsoleOut.Write("  [may contain saves]", ConsoleColor.DarkYellow);
                if (finding.NeedsAdmin && !Elevation.IsElevated) ConsoleOut.Write("  [needs admin]", ConsoleColor.DarkYellow);
                Console.WriteLine();

                string indent = new(' ', 20);
                ConsoleOut.WriteLine(indent + ConsoleOut.ShortenMiddle(finding.Path, width - indent.Length - 1), ConsoleColor.Gray);
                ConsoleOut.WriteLine(indent + ConsoleOut.Truncate(finding.Explanation, width - indent.Length - 1), ConsoleColor.DarkGray);
                if (finding.AdviceCommand is not null)
                    ConsoleOut.WriteLine(indent + "Run: " + finding.AdviceCommand, ConsoleColor.Cyan);
            }
        }

        long safe = findings.Where(f => f.Confidence == Confidence.Safe).Sum(f => f.Size);
        long review = findings.Where(f => f.Confidence == Confidence.Review).Sum(f => f.Size);
        ConsoleOut.Heading("Summary");
        ConsoleOut.WriteLine($"Safe to clean:      {ByteSize.Format(safe)}", ConsoleColor.Green);
        ConsoleOut.WriteLine($"Worth a look:       {ByteSize.Format(review)}", ConsoleColor.Yellow);
        ConsoleOut.WriteLine("Open the DiskSweeper app to select and clean these.");
        return 0;
    }

    private static string Label(Confidence confidence) => confidence switch
    {
        Confidence.Safe => "Safe",
        Confidence.Review => "Review",
        _ => "Windows",
    };

    private static ConsoleColor ColorOf(Confidence confidence) => confidence switch
    {
        Confidence.Safe => ConsoleColor.Green,
        Confidence.Review => ConsoleColor.Yellow,
        _ => ConsoleColor.Cyan,
    };
}
