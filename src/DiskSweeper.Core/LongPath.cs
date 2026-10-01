namespace DiskSweeper.Core;

/// <summary>
/// Helpers for the <c>\\?\</c> path prefix. With it, Windows skips its legacy path rules: paths may be
/// longer than 260 characters, and names ending in a dot or space (which Explorer can't delete) work.
/// </summary>
public static class LongPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>Adds the <c>\\?\</c> prefix to a full path. Paths that already have it are returned unchanged.</summary>
    public static string ToExtended(string fullPath)
    {
        if (fullPath.StartsWith(Prefix, StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return UncPrefix + fullPath[2..];
        return Prefix + fullPath;
    }

    /// <summary>Removes the <c>\\?\</c> prefix so a path can be shown to people or handed to other programs.</summary>
    public static string ToNormal(string path)
    {
        if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path[UncPrefix.Length..];
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path[Prefix.Length..];
        return path;
    }

    /// <summary>
    /// Turns user input into a clean full path: strips quotes, treats "C:" as the drive root (on its own it
    /// means "the current folder on C:"), and removes a trailing backslash except on a drive root.
    /// </summary>
    public static string Normalize(string path)
    {
        // Typing "C:\" in cmd passes C:" because \" escapes the quote, so strip stray quotes too.
        path = path.Trim().Trim('"').Trim();
        if (path.Length == 2 && path[1] == ':') path += Path.DirectorySeparatorChar;
        path = ToNormal(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // GetFullPath also strips trailing dots and spaces from names, which would turn a real folder called
        // "save " into a different one. So only use it when the path is relative or contains . or .. parts.
        bool needsResolving = !Path.IsPathFullyQualified(path)
            || path.Split(Path.DirectorySeparatorChar).Any(part => part is "." or "..");
        if (needsResolving) path = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or anywhere inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        path = Path.TrimEndingDirectorySeparator(path);
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == folder.Length
            || path[folder.Length] == Path.DirectorySeparatorChar
            || folder.EndsWith(Path.DirectorySeparatorChar);
    }
}
