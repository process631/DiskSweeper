namespace DiskSweeper.Core.Deletion;

/// <summary>
/// The places DiskSweeper refuses to delete, no matter what is selected. Three kinds of rule:
/// <list type="bullet">
/// <item>Protected trees: nothing inside may be deleted (C:\Windows, WindowsApps, System Volume Information…).</item>
/// <item>Protected folders: the folder itself must stay, but its contents may go (drive roots, your user folders, Temp…).</item>
/// <item>Anything that contains one of the above, so deleting C:\Users can't sneak past the rule for your profile.</item>
/// </list>
/// </summary>
public static class ProtectedPaths
{
    private static readonly string[] RootSystemFiles =
        ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT", "BOOTSECT.BAK"];

    // Windows parts that live under Program Files. Removing them breaks Windows features rather than freeing junk.
    private static readonly string[] WindowsProgramFolders =
    [
        "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "WindowsPowerShell", "Internet Explorer", "Microsoft Update Health Tools",
        "Reference Assemblies", "Uninstall Information",
    ];

    private static readonly List<string> Trees = [];
    private static readonly List<string> AllowedInsideTrees = [];
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase);

    static ProtectedPaths()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string users = Path.GetDirectoryName(profile) ?? @"C:\Users";

        AddTree(windows);
        AddTree(Path.Join(local, @"Microsoft\Windows"));   // Holds your Start menu, shell data and a registry hive.
        AddTree(Path.Join(programData, @"Microsoft\Windows"));
        foreach (string folder in WindowsProgramFolders)
        {
            AddTree(Path.Join(programFiles, folder));
            AddTree(Path.Join(programFilesX86, folder));
        }
        AddTree(AppContext.BaseDirectory);
        AddTree(AppDataFolder);

        // Contents of these may be cleaned even though they sit inside a protected tree.
        AddAllowedInside(Path.Join(windows, "Temp"));
        AddAllowedInside(Path.Join(windows, @"SoftwareDistribution\Download"));
        AddAllowedInside(Path.Join(windows, "Minidump"));
        AddAllowedInside(Path.Join(windows, "LiveKernelReports"));
        AddAllowedInside(Path.Join(local, @"Microsoft\Windows\INetCache"));
        AddAllowedInside(Path.Join(local, @"Microsoft\Windows\WER"));
        AddAllowedInside(Path.Join(programData, @"Microsoft\Windows\WER"));
        AllowedFiles.Add(Path.Join(windows, "MEMORY.DMP")); // Crash dump: can be gigabytes, only useful for debugging.

        foreach (var drive in SafeGetDrives())
        {
            string root = drive.RootDirectory.FullName;
            Folders.Add(root);
            foreach (string system in new[] { "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "Config.Msi" })
                AddTree(Path.Join(root, system));
        }

        foreach (string folder in new[]
        {
            users, profile, local, roaming, Path.Join(profile, @"AppData"), Path.Join(profile, @"AppData\LocalLow"),
            programData, programFiles, programFilesX86, Path.Join(programFiles, "Common Files"), Path.Join(programFilesX86, "Common Files"),
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.Join(windows, "Temp"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
            Path.Join(profile, "Downloads"), Path.Join(profile, "Saved Games"), Path.Join(profile, "OneDrive"),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"),
        })
        {
            if (!string.IsNullOrEmpty(folder)) Folders.Add(Path.TrimEndingDirectorySeparator(folder));
        }

        // Every profile folder (C:\Users\someone), not only the current user's.
        try
        {
            foreach (string user in Directory.EnumerateDirectories(users)) Folders.Add(user);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Where DiskSweeper keeps its own log and saved scans.</summary>
    public static string AppDataFolder { get; } =
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSweeper");

    /// <summary>Returns why a path must not be deleted, or null if it may be.</summary>
    public static string? WhyProtected(string path)
    {
        string full;
        try
        {
            full = LongPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That isn't a valid path.";
        }

        string? parent = Path.GetDirectoryName(full);
        if (parent is null) return "That is a whole drive.";

        string name = Path.GetFileName(full);
        if (Path.GetDirectoryName(parent) is null && RootSystemFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return name.ToLowerInvariant() switch
            {
                "hiberfil.sys" => "Windows uses this for hibernation and Fast Startup. To remove it, run 'powercfg /h off' in an administrator terminal.",
                "pagefile.sys" or "swapfile.sys" => "Windows uses this as extra memory. Change its size in System Properties > Advanced > Performance > Virtual memory.",
                _ => "Windows needs this file to start up.",
            };
        }

        if (Folders.Contains(full))
            return "This is a Windows or user system folder. You can delete things inside it, but not the folder itself.";

        if (AllowedFiles.Contains(full)) return null;

        foreach (string allowed in AllowedInsideTrees)
        {
            if (IsStrictlyInside(full, allowed)) return null;
        }

        foreach (string tree in Trees)
        {
            if (LongPath.IsSameOrInside(full, tree)) return $"It's inside {tree}, which Windows or DiskSweeper needs.";
        }

        foreach (string protectedPath in Trees.Concat(Folders).Concat(AllowedInsideTrees))
        {
            if (IsStrictlyInside(protectedPath, full)) return $"It contains {protectedPath}, which must not be deleted.";
        }

        return null;
    }

    private static bool IsStrictlyInside(string path, string folder) =>
        path.Length > Path.TrimEndingDirectorySeparator(folder).Length && LongPath.IsSameOrInside(path, folder);

    private static void AddTree(string path)
    {
        if (!string.IsNullOrEmpty(path)) Trees.Add(Path.TrimEndingDirectorySeparator(path));
    }

    private static void AddAllowedInside(string path)
    {
        AllowedInsideTrees.Add(path);
        Folders.Add(path);
    }

    private static IEnumerable<DriveInfo> SafeGetDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (IOException)
            {
                ready = false;
            }
            if (ready) yield return drive;
        }
    }
}
