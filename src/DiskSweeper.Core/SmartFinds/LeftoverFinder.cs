namespace DiskSweeper.Core.SmartFinds;

/// <summary>
/// Finds folders of programs and games that look uninstalled. A folder only counts as a leftover when
/// nothing claims it: no installed program, game, service, running app or PATH entry points into it, its
/// name doesn't match any of them, and it isn't a well-known shared or Windows folder.
/// </summary>
internal sealed class LeftoverFinder
{
    // Shared, system or vendor folders that are normal even when no single program "owns" them.
    private static readonly HashSet<string> AlwaysKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft.NET", "Microsoft Office", "Microsoft SDKs", "Microsoft Visual Studio", "Microsoft Update Health Tools",
        "Windows Kits", "Windows Defender", "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsApps", "WindowsPowerShell", "ModifiableWindowsApps",
        "Common Files", "Internet Explorer", "MSBuild", "Reference Assemblies", "Uninstall Information", "dotnet", "IIS", "IIS Express",
        "Package Cache", "Packages", "PackageManagement", "regid.1991-06.com.microsoft", "ssh", "USOPrivate", "USOShared",
        "SoftwareDistribution", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "PlaceholderTileLogoFolder",
        "Programs", "Publishers", "VirtualStore", "History", "IsolatedStorage", "PeerDistRepub", "speech", "Temp", "ElevatedDiagnostics",
        "SquirrelTemp", "CEF", "fontconfig", "Diagnostics", "Identities", "Media Center Programs", "Macromedia", "Sun", "Oracle",
        "Intel", "AMD", "NVIDIA", "NVIDIA Corporation", "Realtek", "Dell", "HP", "Lenovo", "ASUS", "Unity", "Google", "Mozilla", "Adobe",
        "npm", "npm-cache", "pip", "Python", "NuGet", "nvm", "Yarn", "pnpm", "node-gyp", "jupyter", "pypa", "uv", "Docker",
        "DiskSweeper", "Epic", "EpicGamesLauncher", "UnrealEngine", "Steam",
        "Microsoft OneDrive", "OneDrive", "MicrosoftEdge", "Application Data", "Desktop", "Documents", "Start Menu", "Templates",
        "Favorites", "Windows App Certification Kit", "WindowsHolographicDevices", "MicrosoftWindows",
    };

    private readonly InstalledSoftware _software;
    private readonly List<string> _knownFolders;
    private readonly List<string> _knownNames;
    private readonly HashSet<string> _skipFolders;

    public LeftoverFinder(InstalledSoftware software, IEnumerable<LauncherState> launchers, IEnumerable<string> alreadyFound)
    {
        _software = software;
        var launcherList = launchers.ToList();

        _knownFolders = software.Programs.SelectMany(p => p.RelatedFolders)
            .Concat(software.ServiceFolders)
            .Concat(software.RunningFolders)
            .Concat(software.PathFolders)
            .Concat(launcherList.SelectMany(l => l.Games).Select(g => g.InstallPath).OfType<string>())
            .Concat(launcherList.SelectMany(l => l.GameRoots))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _knownNames = software.Programs.Select(p => p.DisplayName)
            .Concat(software.Programs.Select(p => p.Publisher).OfType<string>())
            .Concat(software.ServiceNames)
            .Concat(StoreAppNames(software.StorePackageFamilies))
            .Concat(VendorNames(_knownFolders))
            .Concat(launcherList.SelectMany(l => l.Games).Select(g => g.Name))
            .Concat(launcherList.SelectMany(l => l.Games).Select(g => g.InstallPath is null ? null : Path.GetFileName(g.InstallPath)).OfType<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _skipFolders = new HashSet<string>(alreadyFound, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<Finding> Find()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localLow = Path.Join(profile, @"AppData\LocalLow");
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var places = new (string Root, string Where, FindCategory Category)[]
        {
            (programFiles, "Program Files", FindCategory.ProgramLeftover),
            (programFilesX86, "Program Files (x86)", FindCategory.ProgramLeftover),
            (programData, "ProgramData", FindCategory.ProgramLeftover),
            (roaming, "AppData\\Roaming", FindCategory.ProgramLeftover),
            (local, "AppData\\Local", FindCategory.ProgramLeftover),
            (Path.Join(local, "Programs"), "AppData\\Local\\Programs", FindCategory.ProgramLeftover),
            (localLow, "AppData\\LocalLow", FindCategory.GameSaves),
            (Path.Join(documents, "My Games"), "Documents\\My Games", FindCategory.GameSaves),
            (Path.Join(profile, "Saved Games"), "Saved Games", FindCategory.GameSaves),
        };

        foreach (var (root, where, category) in places)
        {
            foreach (var folder in SafeFolders(root))
            {
                string name = Path.GetFileName(folder);
                if (_skipFolders.Contains(folder) || AlwaysKeep.Contains(name) || IsWindowsOwned(name) || name.StartsWith('.') || IsClaimed(folder, name)) continue;

                bool saves = category == FindCategory.GameSaves;
                yield return new Finding
                {
                    Id = "leftover:" + folder,
                    Title = saves ? $"Saves/settings: {name}" : $"Possible leftover: {name}",
                    Path = folder,
                    Category = category,
                    Confidence = Confidence.Review,
                    MayContainSaves = saves,
                    Explanation = saves
                        ? $"A game folder in {where}, but no installed game or program matches \"{name}\". If you won't play it again, its saves and settings can go."
                        : $"A folder in {where} that no installed program, game, service or running app uses. It's usually left behind by an uninstaller.",
                };
            }
        }

        foreach (var finding in OrphanedStoreAppData(local)) yield return finding;
    }

    /// <summary>%LOCALAPPDATA%\Packages\&lt;family&gt; folders of Microsoft Store apps that are no longer installed.</summary>
    private IEnumerable<Finding> OrphanedStoreAppData(string local)
    {
        if (_software.StorePackageFamilies.Count == 0) yield break; // Couldn't read the installed list, so don't guess.

        foreach (string folder in SafeFolders(Path.Join(local, "Packages")))
        {
            string family = Path.GetFileName(folder);
            bool firstParty = family.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || family.StartsWith("windows.", StringComparison.OrdinalIgnoreCase);
            if (firstParty || !family.Contains('_') || _software.StorePackageFamilies.Contains(family) || _skipFolders.Contains(folder)) continue;

            yield return new Finding
            {
                Id = "store-leftover:" + folder,
                Title = $"Store app data: {family.Split('_')[0]}",
                Path = folder,
                Category = FindCategory.ProgramLeftover,
                Confidence = Confidence.Review,
                Explanation = "Data of a Microsoft Store app that is no longer installed for you.",
            };
        }
    }

    /// <summary>Folders like "WindowsOobeAppHost" or "MicrosoftEdge" belong to Windows itself.</summary>
    private static bool IsWindowsOwned(string name) =>
        name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Store apps don't appear in the normal uninstall list, so their names come from package families:
    /// "DellInc.AlienwareCommandCenter_htrsf667h5kn2" gives "DellInc" and "AlienwareCommandCenter".
    /// </summary>
    internal static IEnumerable<string> StoreAppNames(IEnumerable<string> families) =>
        families.SelectMany(family => family.Split('_')[0].Split('.')).Where(part => part.Length >= 4);

    /// <summary>
    /// The vendor folder of everything known to be in use: a running service in Program Files\Razer means
    /// ProgramData\Razer and AppData\Razer belong to installed software too.
    /// </summary>
    private static IEnumerable<string> VendorNames(IEnumerable<string> knownFolders)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        ];
        foreach (string folder in knownFolders)
        {
            foreach (string root in roots)
            {
                if (folder.Length <= root.Length + 1 || !LongPath.IsSameOrInside(folder, root)) continue;
                string rest = folder[(root.Length + 1)..];
                int slash = rest.IndexOf(Path.DirectorySeparatorChar);
                yield return slash < 0 ? rest : rest[..slash];
            }
        }
    }

    private bool IsClaimed(string folder, string name)
    {
        foreach (string known in _knownFolders)
        {
            if (LongPath.IsSameOrInside(known, folder) || LongPath.IsSameOrInside(folder, known)) return true;
        }
        foreach (string known in _knownNames)
        {
            if (NameMatcher.Matches(name, known)) return true;
        }
        return false;
    }

    private static IEnumerable<string> SafeFolders(string root)
    {
        try
        {
            return FileSystemWalk.List(root).Where(e => e.IsDirectory && !e.IsLink).Select(e => e.Path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
