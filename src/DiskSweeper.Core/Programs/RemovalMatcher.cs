using DiskSweeper.Core.Deletion;

namespace DiskSweeper.Core.Programs;

/// <summary>
/// Chooses what removing one program would touch. Install folders come from the catalog. Leftovers have
/// to match the product name (not the publisher alone), must not be a shared or Windows folder, and must
/// not belong to any other installed program. Anything uncertain is left out, or listed and refused.
/// </summary>
public static class RemovalMatcher
{
    public static RemovalPlan Match(
        CatalogApp target,
        IReadOnlyList<CatalogApp> others,
        IEnumerable<string> folders,
        IEnumerable<ShortcutHint> shortcuts,
        IEnumerable<string> registryKeys)
    {
        var items = new List<RemovalItem>();
        var keptFolders = new List<string>();

        void ConsiderFolder(string? raw, RemovalKind kind, string reason, bool claimed, bool rejectGeneric)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string path;
            try
            {
                path = LongPath.Normalize(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (items.Any(item => item.Kind is RemovalKind.InstallFolder or RemovalKind.LeftoverFolder
                && item.Location.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return;

            string? refusal = WhyRefuse(path, others, claimed, rejectGeneric);
            bool selected = refusal is null;
            items.Add(new RemovalItem
            {
                Kind = kind,
                Title = Path.GetFileName(path),
                Location = path,
                Reason = reason,
                SelectedByDefault = selected,
                Refusal = refusal,
            });
            if (selected) keptFolders.Add(path);
        }

        if (!string.IsNullOrWhiteSpace(target.InstallLocation))
            ConsiderFolder(target.InstallLocation, RemovalKind.InstallFolder, "Install folder from the program's own entry.", claimed: true, rejectGeneric: true);
        foreach (string owned in target.OwnedPaths)
            ConsiderFolder(owned, RemovalKind.InstallFolder, "Folder that belongs to this program (a version folder, shader cache or similar).", claimed: true, rejectGeneric: false);

        foreach (string folder in folders)
        {
            if (!LeafMatches(folder, target, others)) continue;
            if (IsSharedContainer(folder)) continue;
            ConsiderFolder(folder, RemovalKind.LeftoverFolder, $"The folder name matches {target.Name}.", claimed: false, rejectGeneric: false);
        }

        var ourPaths = ClaimedPaths(target).Concat(keptFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var shortcut in shortcuts)
        {
            if (string.IsNullOrWhiteSpace(shortcut.Path)) continue;
            string path;
            try
            {
                path = LongPath.Normalize(shortcut.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!ShortcutMatches(path, shortcut.Target, target, others, ourPaths)) continue;
            string? refusal = ProtectedPaths.WhyProtected(path);
            items.Add(new RemovalItem
            {
                Kind = RemovalKind.Shortcut,
                Title = Path.GetFileName(path),
                Location = path,
                Reason = shortcut.Target is { Length: > 0 } targetPath && ourPaths.Any(folder => LongPath.IsSameOrInside(targetPath, folder))
                    ? "Shortcut points at this program's folder."
                    : $"Shortcut name matches {target.Name}.",
                SelectedByDefault = refusal is null,
                Refusal = refusal,
            });
        }

        foreach (string key in registryKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            bool ownUninstall = RegistryKeys.SameKey(key, target.UninstallKey);
            if (!ownUninstall && IsUnderUninstallKey(key)) continue;
            if (!ownUninstall && !RegistryLeafMatches(key, target, others)) continue;

            string? refusal = RegistryKeys.WhyUnsafe(key);
            items.Add(new RemovalItem
            {
                Kind = RemovalKind.RegistryKey,
                Title = key.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? key,
                Location = key.Trim(),
                Reason = ownUninstall ? "This program's uninstall entry." : $"Registry key name matches {target.Name}.",
                SelectedByDefault = refusal is null,
                Refusal = refusal,
            });
        }

        string? note = RemovalNames.IsTooGeneric(target.Name)
            ? $"\"{target.Name}\" is too generic to search for leftover folders. Only its install folder, shortcuts that point at it, and its own uninstall entry are listed."
            : null;

        return new RemovalPlan { Items = Collapse(items), Note = note };
    }

    /// <summary>Launcher libraries, package roots and other folders that hold more than one program.</summary>
    public static bool IsSharedContainer(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        if (name.Equals("Epic Games", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Common Files", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return true;

        string? parent = Path.GetFileName(Path.GetDirectoryName(trimmed));
        if (name.Equals("common", StringComparison.OrdinalIgnoreCase) && parent?.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (name.Equals("Games", StringComparison.OrdinalIgnoreCase) && parent?.Equals("GOG Galaxy", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string[] known =
        [
            Path.Join(local, "Packages"),
            Path.Join(local, "Programs"),
            Path.Join(local, "Temp"),
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            programData,
            Path.Join(programData, "Epic"),
            Path.Join(programData, @"Epic\EpicGamesLauncher"),
            Path.Join(local, "Microsoft"),
            Path.Join(roaming, "Microsoft"),
            Path.Join(programData, "Microsoft"),
        ];
        return known.Any(folder => folder.Length > 0 && trimmed.Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A registered install location that is really a shared parent (Program Files, a folder named Games, a launcher root).</summary>
    public static bool IsBroadLocation(string path)
    {
        if (IsSharedContainer(path)) return true;
        if (ProtectedPaths.WhyProtected(path) is not null) return true;
        string norm = RemovalNames.Normalize(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        return norm.Length < 4 || GenericFolder(norm);
    }

    private static bool GenericFolder(string normalized) =>
        normalized is "game" or "games" or "app" or "apps" or "data" or "cache" or "temp" or "tmp"
            or "common" or "shared" or "software" or "tool" or "tools" or "program" or "programs"
            or "windows" or "microsoft" or "system" or "files" or "bin" or "update" or "installer";

    private static string? WhyRefuse(string path, IReadOnlyList<CatalogApp> others, bool claimed, bool rejectGeneric)
    {
        if (ProtectedPaths.WhyProtected(path) is string why) return why;
        if (IsSharedContainer(path)) return "This is a shared launcher or system folder, not this program's own files.";
        if (rejectGeneric && IsBroadLocation(path)) return "The install location is a shared or generic folder, so it is left alone.";
        if (OverlapsAnotherApp(path, others)) return "Another installed program uses this folder, so it is left alone.";
        if (claimed && Path.GetFileName(path).Length == 0) return "That isn't a specific folder.";
        return null;
    }

    private static bool LeafMatches(string folder, CatalogApp target, IReadOnlyList<CatalogApp> others)
    {
        string leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (!RemovalNames.MatchesProduct(leaf, target.Name, target.Publisher)) return false;
        return !others.Any(other => RemovalNames.MatchesProduct(leaf, other.Name, other.Publisher));
    }

    private static bool ShortcutMatches(string path, string? targetPath, CatalogApp target, IReadOnlyList<CatalogApp> others, IReadOnlyList<string> ourPaths)
    {
        if (targetPath is { Length: > 0 } aimed)
        {
            string aimedPath;
            try
            {
                aimedPath = LongPath.Normalize(aimed);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                aimedPath = aimed;
            }
            if (others.SelectMany(ClaimedPaths).Any(folder => LongPath.IsSameOrInside(aimedPath, folder))) return false;
            if (ourPaths.Any(folder => LongPath.IsSameOrInside(aimedPath, folder))) return true;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        if (!RemovalNames.MatchesProduct(name, target.Name, target.Publisher)) return false;
        return !others.Any(other => RemovalNames.MatchesProduct(name, other.Name, other.Publisher));
    }

    private static bool RegistryLeafMatches(string key, CatalogApp target, IReadOnlyList<CatalogApp> others)
    {
        string leaf = key.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (!RemovalNames.MatchesProduct(leaf, target.Name, target.Publisher)) return false;
        return !others.Any(other => RemovalNames.MatchesProduct(leaf, other.Name, other.Publisher));
    }

    private static bool IsUnderUninstallKey(string key) =>
        key.Contains(@"\Uninstall\", StringComparison.OrdinalIgnoreCase);

    private static bool OverlapsAnotherApp(string path, IReadOnlyList<CatalogApp> others)
    {
        foreach (string other in others.SelectMany(ClaimedPaths))
        {
            if (path.Equals(other, StringComparison.OrdinalIgnoreCase)) return true;
            if (LongPath.IsSameOrInside(other, path)) return true;
            if (LongPath.IsSameOrInside(path, other)) return true;
        }
        return false;
    }

    /// <summary>Paths this app claims, ignoring install locations that are really a shared parent.</summary>
    public static IEnumerable<string> ClaimedPaths(CatalogApp app)
    {
        if (app.InstallLocation is { Length: > 0 } install && !IsBroadLocation(install) && ProtectedPaths.WhyProtected(install) is null)
            yield return NormalizeQuiet(install);
        foreach (string owned in app.OwnedPaths)
        {
            if (owned.Length == 0 || IsSharedContainer(owned) || ProtectedPaths.WhyProtected(owned) is not null) continue;
            yield return NormalizeQuiet(owned);
        }
    }

    private static string NormalizeQuiet(string path)
    {
        try
        {
            return LongPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>Drops items that sit inside a folder already selected, so a shortcut inside a matched folder isn't listed twice.</summary>
    private static List<RemovalItem> Collapse(List<RemovalItem> items)
    {
        var parents = items
            .Where(item => item.Refusal is null && item.Kind is RemovalKind.InstallFolder or RemovalKind.LeftoverFolder)
            .Select(item => item.Location)
            .ToList();
        return items.Where(item =>
        {
            if (item.Kind is RemovalKind.RegistryKey) return true;
            return !parents.Any(parent =>
                !parent.Equals(item.Location, StringComparison.OrdinalIgnoreCase)
                && LongPath.IsSameOrInside(item.Location, parent));
        }).ToList();
    }
}
