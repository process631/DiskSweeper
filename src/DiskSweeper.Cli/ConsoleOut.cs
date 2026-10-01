namespace DiskSweeper.Cli;

/// <summary>Small helpers for colored, width-aware console output. Colors are skipped when output goes to a file.</summary>
internal static class ConsoleOut
{
    public static readonly bool UseColor = !Console.IsOutputRedirected;

    public static int Width
    {
        get
        {
            if (Console.IsOutputRedirected) return 160;
            try
            {
                return Math.Max(60, Console.WindowWidth);
            }
            catch (IOException)
            {
                return 120;
            }
        }
    }

    public static void Write(string text, ConsoleColor? color = null)
    {
        if (color is null || !UseColor)
        {
            Console.Write(text);
            return;
        }
        Console.ForegroundColor = color.Value;
        Console.Write(text);
        Console.ResetColor();
    }

    public static void WriteLine(string text = "", ConsoleColor? color = null)
    {
        Write(text, color);
        Console.WriteLine();
    }

    public static void Heading(string text)
    {
        Console.WriteLine();
        WriteLine(text, ConsoleColor.Cyan);
        WriteLine(new string('─', Math.Min(text.Length, Width - 1)), ConsoleColor.DarkCyan);
    }

    /// <summary>Red for 10 GB and up, yellow for 1 GB and up, so the big stuff jumps out.</summary>
    public static ConsoleColor? SizeColor(long bytes) =>
        bytes >= 10L << 30 ? ConsoleColor.Red : bytes >= 1L << 30 ? ConsoleColor.Yellow : null;

    /// <summary>Shortens text to <paramref name="max"/> characters by cutting the end and adding "…".</summary>
    public static string Truncate(string text, int max) =>
        max <= 0 ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Shortens a path by cutting the middle, keeping the drive and the file name visible.</summary>
    public static string ShortenMiddle(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max < 5) return Truncate(text, max);
        int keepEnd = max * 2 / 3;
        int keepStart = max - keepEnd - 1;
        return text[..keepStart] + "…" + text[^keepEnd..];
    }

    /// <summary>A bar of block characters (with 1/8 steps) showing <paramref name="fraction"/> of <paramref name="width"/>.</summary>
    public static string Bar(double fraction, int width)
    {
        const string partials = "▏▎▍▌▋▊▉";
        double cells = Math.Clamp(fraction, 0, 1) * width;
        int full = (int)cells;
        int eighths = (int)((cells - full) * 8);
        string bar = new('█', full);
        if (eighths > 0 && full < width) bar += partials[eighths - 1];
        return bar;
    }
}
