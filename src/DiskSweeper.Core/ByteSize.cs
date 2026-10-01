using System.Globalization;

namespace DiskSweeper.Core;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count the way File Explorer does (1 KB = 1024 bytes), e.g. "12.3 GB".</summary>
    public static string Format(long bytes, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (Math.Abs(bytes) < 1024) return string.Format(culture, "{0} B", bytes);

        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(culture, "{0:0.0} {1}", value, Units[unit]);
    }

    /// <summary>Formats a change in size with an explicit sign, e.g. "+1.2 GB" or "-300.0 MB".</summary>
    public static string FormatDelta(long bytes, IFormatProvider? culture = null) =>
        bytes > 0 ? "+" + Format(bytes, culture) : bytes < 0 ? "-" + Format(-bytes, culture) : Format(0, culture);

    /// <summary>Parses sizes like "500", "500 MB", "1.5gb" or "2 TB". Returns false for anything else.</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim().ToUpperInvariant();
        int split = 0;
        while (split < s.Length && (char.IsDigit(s[split]) || s[split] is '.' or ',')) split++;

        if (!double.TryParse(s[..split].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return false;

        string unit = s[split..].Trim();
        if (unit.Length > 1 && unit.EndsWith('B')) unit = unit[..^1];
        int power = unit switch
        {
            "" or "B" => 0,
            "K" => 1,
            "M" => 2,
            "G" => 3,
            "T" => 4,
            _ => -1,
        };
        if (power < 0 || number < 0) return false;

        bytes = (long)(number * Math.Pow(1024, power));
        return true;
    }
}
