namespace DiskSweeper.Core.Deletion;

/// <summary>Things worth knowing before deleting something that is allowed but might be a mistake.</summary>
public static class PathWarnings
{
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static readonly string[] SaveFolders =
    [
        Path.Join(Documents, "My Games"),
        Path.Join(Profile, "Saved Games"),
        Path.Join(Profile, @"AppData\LocalLow"),
    ];

    private static readonly string[] CloudFolders =
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Append(Path.Join(Profile, "OneDrive"))
            .OfType<string>()
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static readonly string[] ProgramFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    private static readonly string[] AppDataFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ];

    public static IReadOnlyList<string> For(string path)
    {
        var warnings = new List<string>();

        if (CloudFolders.Any(cloud => LongPath.IsSameOrInside(path, cloud)))
            warnings.Add("This is in OneDrive. Deleting it here also deletes it from OneDrive on the web and your other devices.");

        if (MayContainSaves(path))
            warnings.Add("This may contain game saves. Games with cloud saves (Steam Cloud, etc.) keep a copy online; others don't.");

        if (ProgramFolders.Any(folder => IsStrictlyInside(path, folder)))
            warnings.Add("This is in Program Files. If the program is still installed, uninstall it instead so it's removed cleanly.");

        if (AppDataFolders.Any(folder => IsDirectlyInside(path, folder)))
            warnings.Add("This is an app's settings folder. If the app is still installed, it will start fresh next time.");

        return warnings;
    }

    public static bool MayContainSaves(string path)
    {
        if (SaveFolders.Any(folder => IsStrictlyInside(path, folder))) return true;
        string name = Path.GetFileName(path);
        return name.Contains("save", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictlyInside(string path, string folder) =>
        path.Length > folder.Length && LongPath.IsSameOrInside(path, folder);

    private static bool IsDirectlyInside(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)), folder, StringComparison.OrdinalIgnoreCase);
}
