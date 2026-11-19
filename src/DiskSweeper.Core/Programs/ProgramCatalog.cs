using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.Core.Programs;

/// <summary>
/// Installed programs and games from the uninstall registry (HKLM and HKCU, 32-bit and 64-bit),
/// Steam, Epic, GOG, Roblox's per-user install, and Microsoft Store packages.
/// </summary>
public static partial class ProgramCatalog
{
    private static readonly string[] UpdateReleases =
        ["Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack", "ServicePack"];

    public static IReadOnlyList<CatalogApp> Load()
    {
        var raw = new List<CatalogApp>();
        raw.AddRange(Safe(LoadRegistry));
        raw.AddRange(Safe(LoadSteam));
        raw.AddRange(Safe(LoadEpic));
        raw.AddRange(Safe(LoadGog));
        raw.AddRange(Safe(LoadRoblox));
        raw.AddRange(Safe(LoadStore));
        return Merge(raw).Select(Classify).OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when this entry is DiskSweeper itself, which must not uninstall the process that is running.</summary>
    public static bool IsThisApplication(CatalogApp app)
    {
        if (app.Name.Equals("DiskSweeper", StringComparison.OrdinalIgnoreCase)) return true;
        if (app.InstallLocation is not string install) return false;
        try
        {
            string full = LongPath.Normalize(install);
            string mine = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            return full.Equals(mine, StringComparison.OrdinalIgnoreCase)
                || (Path.GetFileName(full).Equals("DiskSweeper", StringComparison.OrdinalIgnoreCase) && LongPath.IsSameOrInside(mine, full));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Folds launcher entries and registry entries for the same program into one. Two products that merely
    /// share a parent folder (Fortnite and Rocket League under Epic Games) stay separate.
    /// </summary>
    public static IReadOnlyList<CatalogApp> Merge(IEnumerable<CatalogApp> apps)
    {
        var merged = new List<CatalogApp>();
        foreach (var app in apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)).OrderBy(a => Rank(a.Source)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            int index = merged.FindIndex(existing => SameApp(existing, app));
            if (index < 0) merged.Add(app);
            else merged[index] = Combine(merged[index], app);
        }
        return merged;
    }

    /// <summary>Registry entries whose folder is clearly a launcher game are labeled as that launcher.</summary>
    public static CatalogApp Classify(CatalogApp app)
    {
        if (app.Source != AppSource.Registry || app.InstallLocation is not string path) return app;
        if (HasSegment(path, "steamapps", "common")) return app with { Source = AppSource.Steam };
        if (HasSegment(path, "Epic Games") && !app.Name.Contains("Launcher", StringComparison.OrdinalIgnoreCase))
            return app with { Source = AppSource.Epic };
        if (HasSegment(path, "GOG Galaxy", "Games")) return app with { Source = AppSource.Gog };
        if (HasSegment(path, "Roblox") && app.Name.Contains("Roblox", StringComparison.OrdinalIgnoreCase))
            return app with { Source = AppSource.Roblox };
        return app;
    }

    private static bool HasSegment(string path, params string[] segments)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i <= parts.Length - segments.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < segments.Length; j++)
            {
                if (!parts[i + j].Equals(segments[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static int Rank(AppSource source) => source switch
    {
        AppSource.Steam => 0,
        AppSource.Epic => 1,
        AppSource.Gog => 2,
        AppSource.Roblox => 3,
        AppSource.MicrosoftStore => 4,
        _ => 5,
    };

    private static bool SameApp(CatalogApp a, CatalogApp b)
    {
        if (SameText(a.SteamAppId, b.SteamAppId) || SameText(a.StoreFamilyName, b.StoreFamilyName)) return true;
        if (SameRoblox(a, b)) return true;

        string? left = Norm(a.InstallLocation);
        string? right = Norm(b.InstallLocation);
        if (left is not null && right is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
        if (left is not null && right is not null && NestedSpecific(left, right)) return true;
        if (!RemovalNames.SameIdentity(a.Name, b.Name)) return false;
        if (left is null || right is null) return true;
        return RemovalMatcher.IsBroadLocation(left) || RemovalMatcher.IsBroadLocation(right);
    }

    private static bool NestedSpecific(string left, string right)
    {
        bool leftInside = LongPath.IsSameOrInside(left, right) && left.Length > right.Length;
        bool rightInside = LongPath.IsSameOrInside(right, left) && right.Length > left.Length;
        if (!leftInside && !rightInside) return false;
        string parent = leftInside ? right : left;
        return !RemovalMatcher.IsBroadLocation(parent);
    }

    private static bool SameRoblox(CatalogApp a, CatalogApp b)
    {
        if (a.Source != AppSource.Roblox && b.Source != AppSource.Roblox) return false;
        bool aStudio = a.Name.Contains("Studio", StringComparison.OrdinalIgnoreCase);
        bool bStudio = b.Name.Contains("Studio", StringComparison.OrdinalIgnoreCase);
        if (aStudio != bStudio) return false;
        string left = RemovalNames.Normalize(a.Name);
        string right = RemovalNames.Normalize(b.Name);
        return left.StartsWith("roblox", StringComparison.Ordinal) && right.StartsWith("roblox", StringComparison.Ordinal);
    }

    private static CatalogApp Combine(CatalogApp primary, CatalogApp secondary)
    {
        string? install = PreferPath(primary.InstallLocation, secondary.InstallLocation);
        var owned = primary.OwnedPaths.Concat(secondary.OwnedPaths)
            .Select(Norm)
            .OfType<string>()
            .Where(path => install is null || !path.Equals(install, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return primary with
        {
            Publisher = PreferPublisher(primary.Publisher, secondary.Publisher),
            Version = First(primary.Version, secondary.Version),
            InstallLocation = install,
            OwnedPaths = owned,
            ListedSizeBytes = Max(primary.ListedSizeBytes, secondary.ListedSizeBytes),
            UninstallString = First(primary.UninstallString, secondary.UninstallString),
            QuietUninstallString = First(primary.QuietUninstallString, secondary.QuietUninstallString),
            MachineWide = primary.MachineWide || secondary.MachineWide,
            UninstallKey = First(primary.UninstallKey, secondary.UninstallKey),
            SteamAppId = First(primary.SteamAppId, secondary.SteamAppId),
            SteamExe = First(primary.SteamExe, secondary.SteamExe),
            StorePackageFullName = First(primary.StorePackageFullName, secondary.StorePackageFullName),
            StoreFamilyName = First(primary.StoreFamilyName, secondary.StoreFamilyName),
        };
    }

    private static string? PreferPath(string? primary, string? secondary)
    {
        string? left = Norm(primary);
        string? right = Norm(secondary);
        if (left is null) return right;
        if (right is null) return left;
        bool leftBroad = RemovalMatcher.IsBroadLocation(left);
        bool rightBroad = RemovalMatcher.IsBroadLocation(right);
        if (leftBroad && !rightBroad) return right;
        if (rightBroad && !leftBroad) return left;
        if (LongPath.IsSameOrInside(left, right) && left.Length > right.Length) return left;
        if (LongPath.IsSameOrInside(right, left) && right.Length > left.Length) return right;
        return left;
    }

    private static string? PreferPublisher(string? primary, string? secondary)
    {
        // A launcher placeholder ("Steam") loses to the publisher the registry recorded.
        string? specific = SpecificPublisher(secondary) ?? SpecificPublisher(primary);
        return specific ?? First(primary, secondary);
    }

    private static string? SpecificPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        string norm = RemovalNames.Normalize(publisher);
        if (norm is "steam" or "epic" or "epicgames" or "gog" or "gogcom" or "goggalaxy" or "valve") return null;
        return publisher;
    }

    private static string? First(string? primary, string? secondary) =>
        string.IsNullOrWhiteSpace(primary) ? (string.IsNullOrWhiteSpace(secondary) ? null : secondary) : primary;

    private static long? Max(long? left, long? right) => (left, right) switch
    {
        (null, null) => null,
        (null, not null) => right,
        (not null, null) => left,
        _ => Math.Max(left!.Value, right!.Value),
    };

    private static bool SameText(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static string? Norm(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return LongPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<CatalogApp> Safe(Func<IEnumerable<CatalogApp>> load)
    {
        try
        {
            return load().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return [];
        }
    }

    private static IEnumerable<CatalogApp> LoadRegistry()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, root, machine) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", true),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false),
            (RegistryHive.CurrentUser, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", false),
        })
        {
            RegistryKey baseKey;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            using (baseKey)
            using (var key = baseKey.OpenSubKey(root))
            {
                if (key is null) continue;
                string hiveName = machine ? "HKLM" : "HKCU";
                foreach (string sub in key.GetSubKeyNames())
                {
                    string keyPath = hiveName + "\\" + root + "\\" + sub;
                    if (!seen.Add(keyPath)) continue;
                    using var entry = key.OpenSubKey(sub);
                    if (entry is null) continue;
                    if (ReadUninstallEntry(entry, keyPath, machine) is CatalogApp app) yield return app;
                }
            }
        }
    }

    private static CatalogApp? ReadUninstallEntry(RegistryKey entry, string keyPath, bool machine)
    {
        if (entry.GetValue("DisplayName") is not string name || name.Trim().Length == 0) return null;
        name = name.Trim();
        if (IsOne(entry.GetValue("SystemComponent"))) return null;
        if (entry.GetValue("ReleaseType") is string release && UpdateReleases.Contains(release, StringComparer.OrdinalIgnoreCase)) return null;
        if (name.StartsWith("Update for ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Hotfix", StringComparison.OrdinalIgnoreCase)
            || KnowledgeBase().IsMatch(name))
            return null;

        string? uninstall = entry.GetValue("UninstallString") as string;
        string? quiet = entry.GetValue("QuietUninstallString") as string;
        string? location = CleanPath(entry.GetValue("InstallLocation") as string);
        if (location is null && uninstall is not null && !uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            location = CleanPath(InstalledSoftware.FolderOfCommand(uninstall));

        return new CatalogApp
        {
            Id = "reg:" + keyPath,
            Name = name,
            Publisher = Blank(entry.GetValue("Publisher") as string),
            Version = Blank(entry.GetValue("DisplayVersion") as string),
            Source = AppSource.Registry,
            InstallLocation = location,
            ListedSizeBytes = EstimatedBytes(entry.GetValue("EstimatedSize")),
            UninstallString = Blank(uninstall),
            QuietUninstallString = Blank(quiet),
            MachineWide = machine,
            UninstallKey = keyPath,
        };
    }

    private static IEnumerable<CatalogApp> LoadSteam()
    {
        string? steam = SteamLauncher.FindInstallFolder();
        if (steam is null) yield break;
        string steamExe = Path.Join(steam, "steam.exe");
        if (!File.Exists(steamExe)) steamExe = "";

        foreach (var library in SteamLauncher.LoadLibraries(steam))
        {
            foreach (var game in library.Games)
            {
                if (string.IsNullOrWhiteSpace(game.Name)) continue;
                var owned = new List<string>();
                if (game.Id is { Length: > 0 } id && game.InstallPath is { Length: > 0 } install)
                {
                    string? steamApps = Path.GetDirectoryName(Path.GetDirectoryName(install));
                    if (steamApps is not null && Path.GetFileName(steamApps).Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                    {
                        AddDirectory(owned, Path.Join(steamApps, "shadercache", id));
                        AddDirectory(owned, Path.Join(steamApps, "workshop", "content", id));
                    }
                }

                yield return new CatalogApp
                {
                    Id = "steam:" + (game.Id ?? game.Name),
                    Name = game.Name,
                    Source = AppSource.Steam,
                    InstallLocation = game.InstallPath,
                    OwnedPaths = owned,
                    ListedSizeBytes = game.SizeBytes,
                    SteamAppId = game.Id,
                    SteamExe = steamExe.Length == 0 ? null : steamExe,
                };
            }
        }
    }

    private static IEnumerable<CatalogApp> LoadEpic()
    {
        foreach (var game in EpicLauncher.Load([]).Games)
        {
            if (string.IsNullOrWhiteSpace(game.Name)) continue;
            yield return new CatalogApp
            {
                Id = "epic:" + (game.Id ?? game.Name),
                Name = game.Name,
                Source = AppSource.Epic,
                InstallLocation = game.InstallPath,
                ListedSizeBytes = game.SizeBytes,
            };
        }
    }

    private static IEnumerable<CatalogApp> LoadGog()
    {
        foreach (var game in GogLauncher.Load([]).Games)
        {
            if (string.IsNullOrWhiteSpace(game.Name)) continue;
            yield return new CatalogApp
            {
                Id = "gog:" + (game.Id ?? game.Name),
                Name = game.Name,
                Source = AppSource.Gog,
                InstallLocation = game.InstallPath,
            };
        }
    }

    private static IEnumerable<CatalogApp> LoadRoblox()
    {
        string versions = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Roblox\Versions");
        if (!Directory.Exists(versions)) yield break;
        var player = VersionFolders(versions, "RobloxPlayerBeta.exe");
        var studio = VersionFolders(versions, "RobloxStudioBeta.exe");
        if (player.Count > 0) yield return RobloxApp("roblox:player", "Roblox", player, "RobloxPlayerInstaller.exe");
        if (studio.Count > 0) yield return RobloxApp("roblox:studio", "Roblox Studio", studio, "RobloxStudioInstaller.exe");
    }

    private static CatalogApp RobloxApp(string id, string name, List<string> folders, string installerName)
    {
        string installer = Path.Join(folders[0], installerName);
        return new CatalogApp
        {
            Id = id,
            Name = name,
            Publisher = "Roblox Corporation",
            Source = AppSource.Roblox,
            InstallLocation = folders[0],
            OwnedPaths = folders.Skip(1).ToList(),
            UninstallString = File.Exists(installer) ? $"\"{installer}\" -uninstall" : null,
        };
    }

    private static List<string> VersionFolders(string versions, string exe)
    {
        try
        {
            return Directory.EnumerateDirectories(versions)
                .Where(dir => File.Exists(Path.Join(dir, exe)))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<CatalogApp> LoadStore()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (key is null) yield break;

        var best = new Dictionary<string, (int Rank, CatalogApp App)>(StringComparer.OrdinalIgnoreCase);
        foreach (string fullName in key.GetSubKeyNames())
        {
            if (!TryParsePackage(fullName, out string name, out string version, out string arch, out string resource, out string publisherId)) continue;
            if (resource.Length > 0 || SkipStorePackage(name)) continue;

            string family = name + "_" + publisherId;
            string? display = null;
            using (var package = key.OpenSubKey(fullName))
            {
                if (package?.GetValue("DisplayName") is string shown && shown.Length > 0 && !shown.StartsWith("@{", StringComparison.Ordinal))
                    display = shown;
            }

            string data = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family);
            var app = new CatalogApp
            {
                Id = "store:" + family,
                Name = display ?? FriendlyStoreName(name),
                Publisher = name.Contains('.', StringComparison.Ordinal) ? name[..name.LastIndexOf('.')] : null,
                Version = version,
                Source = AppSource.MicrosoftStore,
                OwnedPaths = Directory.Exists(data) ? [data] : [],
                StorePackageFullName = fullName,
                StoreFamilyName = family,
            };

            int rank = arch.ToLowerInvariant() switch { "x64" => 3, "arm64" => 2, "x86" => 1, _ => 0 };
            if (!best.TryGetValue(family, out var existing) || rank > existing.Rank) best[family] = (rank, app);
        }

        foreach (var (_, app) in best.Values) yield return app;
    }

    internal static bool TryParsePackage(string fullName, out string name, out string version, out string arch, out string resource, out string publisher)
    {
        name = version = arch = resource = publisher = "";
        var match = StorePackage().Match(fullName);
        if (!match.Success) return false;
        name = match.Groups["name"].Value;
        version = match.Groups["version"].Value;
        arch = match.Groups["arch"].Value;
        resource = match.Groups["resource"].Value;
        publisher = match.Groups["publisher"].Value;
        return name.Length > 0 && publisher.Length > 0;
    }

    private static bool SkipStorePackage(string name) =>
        name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("windows.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MicrosoftWindows", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Framework", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VCLibs", StringComparison.OrdinalIgnoreCase)
        || name.Contains("WinJS", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyStoreName(string packageName)
    {
        string leaf = packageName.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? packageName;
        var builder = new System.Text.StringBuilder(leaf.Length + 4);
        for (int i = 0; i < leaf.Length; i++)
        {
            if (i > 0 && char.IsUpper(leaf[i]) && !char.IsUpper(leaf[i - 1])) builder.Append(' ');
            builder.Append(leaf[i]);
        }
        return builder.ToString();
    }

    private static void AddDirectory(List<string> paths, string path)
    {
        if (Directory.Exists(path)) paths.Add(path);
    }

    private static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"').Trim();
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            string full = LongPath.Normalize(path);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return LongPath.IsSameOrInside(full, windows) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsOne(object? value) => value is int number ? number == 1 : value is long longer && longer == 1;

    private static long? EstimatedBytes(object? value)
    {
        long? kb = value switch
        {
            int number when number > 0 => number,
            long number when number > 0 => number,
            string text when long.TryParse(text, out long number) && number > 0 => number,
            _ => null,
        };
        return kb is null ? null : kb * 1024L;
    }

    [GeneratedRegex(@"^KB\d{5,}", RegexOptions.IgnoreCase)]
    private static partial Regex KnowledgeBase();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][A-Za-z0-9.]*)_(?<version>\d+(?:\.\d+)*)_(?<arch>[A-Za-z0-9]+)_(?<resource>[^_]*)_(?<publisher>[A-Za-z0-9]+)$")]
    private static partial Regex StorePackage();
}
