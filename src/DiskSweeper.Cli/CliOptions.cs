namespace DiskSweeper.Cli;

internal sealed class UsageException(string message) : Exception(message);

internal sealed class CliOptions
{
    public const string Usage = """
        dsweep - DiskSweeper's command-line tool: see what is taking up space on your PC

        Usage:
          dsweep [scan] [folder] [options]   Show the biggest folders and files
          dsweep finds [options]             List junk and leftovers that are safe to review

        folder              Drive or folder to scan (default: C:\)

        Scan options:
          --depth <n>       Folder levels to show in the tree (default: 4)
          --top <n>         Biggest items to show inside each folder (default: 10)
          --expand <pct>    Only open folders holding at least this % of the scan (default: 2)
          --files <n>       How many of the biggest files to list (default: 50)
          --threads <n>     Folders to read at the same time (default: CPU cores, max 12)
          --fast            Read the NTFS file table directly (needs an administrator terminal)
          --save            Save this scan so the next one can show what changed
          --compare         Show what grew or shrank since the last saved scan of this folder

        Finds options:
          --all             Also list items under 10 MB

          -h, --help        Show this help

        Examples:
          dsweep
          dsweep D:\
          dsweep "C:\Users\me\AppData" --depth 3 --files 20
          dsweep C:\ --fast --save --compare
          dsweep finds
        """;

    public string Command { get; private set; } = "scan";

    public string Path { get; private set; } = @"C:\";

    public int Depth { get; private set; } = 4;

    public int Top { get; private set; } = 10;

    public double ExpandPercent { get; private set; } = 2;

    public int Files { get; private set; } = 50;

    public int? Threads { get; private set; }

    public bool Fast { get; private set; }

    public bool Save { get; private set; }

    public bool Compare { get; private set; }

    public bool All { get; private set; }

    public bool ShowHelp { get; private set; }

    public CliOptions WithoutFast()
    {
        var copy = (CliOptions)MemberwiseClone();
        copy.Fast = false;
        return copy;
    }

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        bool pathSeen = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?":
                    options.ShowHelp = true;
                    break;
                case "scan" or "finds" when i == 0:
                    options.Command = arg.ToLowerInvariant();
                    break;
                case "--depth":
                    options.Depth = ReadInt(args, ref i, arg, min: 0);
                    break;
                case "--top":
                    options.Top = ReadInt(args, ref i, arg, min: 1);
                    break;
                case "--expand":
                    options.ExpandPercent = ReadInt(args, ref i, arg, min: 0);
                    break;
                case "--files":
                    options.Files = ReadInt(args, ref i, arg, min: 0);
                    break;
                case "--threads":
                    options.Threads = ReadInt(args, ref i, arg, min: 1);
                    break;
                case "--fast":
                    options.Fast = true;
                    break;
                case "--save":
                    options.Save = true;
                    break;
                case "--compare":
                    options.Compare = true;
                    break;
                case "--all":
                    options.All = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"Unknown option '{arg}'.");
                    if (pathSeen) throw new UsageException($"Only one folder can be scanned at a time (got '{options.Path}' and '{arg}').");
                    options.Path = arg;
                    pathSeen = true;
                    break;
            }
        }
        return options;
    }

    private static int ReadInt(IReadOnlyList<string> args, ref int i, string name, int min)
    {
        if (i + 1 >= args.Count || !int.TryParse(args[i + 1], out int value) || value < min)
            throw new UsageException($"{name} needs a whole number of at least {min}.");
        i++;
        return value;
    }
}
