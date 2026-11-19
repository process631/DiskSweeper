using System.Text.Json;
using Microsoft.Win32;

namespace DiskSweeper.Core.SmartFinds;

public sealed record InstalledGame(string Launcher, string Name, string? InstallPath, string? Id = null, long? SizeBytes = null);

/// <summary>What one game launcher knows: where its game folders live and which games it has installed.</summary>
public sealed class LauncherState
{
    public required string Name { get; init; }

    /// <summary>False when the launcher itself is gone, which turns every game folder it left behind into a leftover.</summary>
    public bool LauncherInstalled { get; init; }

    public IReadOnlyList<InstalledGame> Games { get; init; } = [];

    /// <summary>Folders that hold one subfolder per game (e.g. steamapps\common, Program Files\Epic Games).</summary>
    public IReadOnlyList<string> GameRoots { get; init; } = [];

    /// <summary>Subfolders of the game roots that belong to the launcher itself rather than to a game.</summary>
    public IReadOnlySet<string> NonGameFolders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Steam: libraries from libraryfolders.vdf, installed games from appmanifest_*.acf.</summary>
public static class SteamLauncher
{
    public sealed record SteamLibrary(string Path, IReadOnlyList<InstalledGame> Games);

    public static string? FindInstallFolder()
    {
        foreach (var (hive, key, value) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            using var sub = hive.OpenSubKey(key);
            if (sub?.GetValue(value) is string path && path.Length > 0)
            {
                string full = LongPath.Normalize(path);
                if (Directory.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>Every Steam library folder with the games installed in it.</summary>
    public static IReadOnlyList<SteamLibrary> LoadLibraries(string steamFolder)
    {
        var libraryPaths = new List<string> { steamFolder };
        var vdf = Vdf.TryLoad(Path.Join(steamFolder, @"steamapps\libraryfolders.vdf"));
        var folders = vdf?.Block("libraryfolders") ?? vdf?.Block("LibraryFolders");
        if (folders is not null)
        {
            foreach (var (_, block) in folders.Blocks)
            {
                if (block["path"] is string path && path.Length > 0) libraryPaths.Add(LongPath.Normalize(path));
            }
        }

        return libraryPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => Directory.Exists(Path.Join(path, "steamapps")))
            .Select(path => new SteamLibrary(path, LoadManifests(path)))
            .ToList();
    }

    public static IReadOnlyList<InstalledGame> LoadManifests(string libraryPath)
    {
        string steamApps = Path.Join(libraryPath, "steamapps");
        var games = new List<InstalledGame>();
        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return games;
        }

        foreach (string manifest in manifests)
        {
            var state = Vdf.TryLoad(manifest)?.Block("AppState");
            if (state?["appid"] is not string appId) continue;
            string name = state["name"] ?? $"App {appId}";
            string? installDir = state["installdir"];
            long? size = state["SizeOnDisk"] is string sizeText && long.TryParse(sizeText, out long bytes) && bytes > 0 ? bytes : null;
            games.Add(new InstalledGame("Steam", name, installDir is null ? null : Path.Join(steamApps, "common", installDir), appId, size));
        }
        return games;
    }
}

/// <summary>Epic Games: installed games are JSON .item manifests under ProgramData.</summary>
public static class EpicLauncher
{
    public static readonly string ManifestFolder = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");

    public static LauncherState Load(IEnumerable<InstalledProgram> programs)
    {
        var games = new List<InstalledGame>();
        if (Directory.Exists(ManifestFolder))
        {
            foreach (string file in SafeFiles(ManifestFolder, "*.item"))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    var root = json.RootElement;
                    string name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? "" : "";
                    string? location = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                    string? id = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                    long? size = ReadSize(root);
                    games.Add(new InstalledGame("Epic", name, location is null ? null : LongPath.Normalize(location), id, size));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                }
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var roots = games.Select(g => g.InstallPath is null ? null : Path.GetDirectoryName(g.InstallPath))
            .Append(Path.Join(programFiles, "Epic Games"))
            .Append(Path.Join(programFilesX86, "Epic Games"))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();

        bool launcherInstalled = Directory.Exists(ManifestFolder)
            || programs.Any(p => p.DisplayName.Equals("Epic Games Launcher", StringComparison.OrdinalIgnoreCase));

        return new LauncherState
        {
            Name = "Epic Games Launcher",
            LauncherInstalled = launcherInstalled,
            Games = games,
            GameRoots = roots,
            NonGameFolders = new HashSet<string>(["Launcher", "DirectXRedist", "Epic Online Services", "EpicOnlineServices"], StringComparer.OrdinalIgnoreCase),
        };
    }

    private static long? ReadSize(JsonElement root)
    {
        if (!root.TryGetProperty("InstallSize", out var size)) return null;
        if (size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out long bytes) && bytes > 0) return bytes;
        if (size.ValueKind == JsonValueKind.String && long.TryParse(size.GetString(), out long parsed) && parsed > 0) return parsed;
        return null;
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(folder, pattern).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>GOG Galaxy: each installed game has a registry key with its path.</summary>
public static class GogLauncher
{
    public static LauncherState Load(IEnumerable<InstalledProgram> programs)
    {
        var games = new List<InstalledGame>();
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
        {
            foreach (string id in key?.GetSubKeyNames() ?? [])
            {
                using var game = key!.OpenSubKey(id);
                string? path = game?.GetValue("path") as string;
                string name = game?.GetValue("gameName") as string ?? game?.GetValue("GAMENAME") as string ?? id;
                games.Add(new InstalledGame("GOG", name, string.IsNullOrEmpty(path) ? null : LongPath.Normalize(path), id));
            }
        }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var roots = games.Select(g => g.InstallPath is null ? null : Path.GetDirectoryName(g.InstallPath))
            .Append(Path.Join(programFilesX86, @"GOG Galaxy\Games"))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();

        return new LauncherState
        {
            Name = "GOG Galaxy",
            LauncherInstalled = games.Count > 0 || programs.Any(p => p.DisplayName.StartsWith("GOG GALAXY", StringComparison.OrdinalIgnoreCase)),
            Games = games,
            GameRoots = roots,
        };
    }
}
