using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DiskSweeper.Core.Programs;

/// <summary>
/// Looks through the usual leftover places (AppData, ProgramData, Documents, saves, temp, Program Files,
/// desktop and Start menu) and asks <see cref="RemovalMatcher"/> which of them belong to one program.
/// It does not walk the whole disk.
/// </summary>
public static class RemovalScanner
{
    public static RemovalPlan Scan(CatalogApp target, IReadOnlyList<CatalogApp> installed, CancellationToken cancellationToken = default)
    {
        var others = installed.Where(app => !app.Id.Equals(target.Id, StringComparison.Ordinal)).ToList();
        var folders = DiscoverFolders(target, cancellationToken);
        var shortcuts = DiscoverShortcuts();
        var keys = DiscoverRegistryKeys(target);
        var plan = RemovalMatcher.Match(target, others, folders, shortcuts, keys);

        var items = new List<RemovalItem>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Kind == RemovalKind.RegistryKey)
            {
                if (item.Refusal is null && !RegistryKeys.Exists(item.Location)) continue;
                items.Add(item);
                continue;
            }

            if (!FileSystemWalk.Exists(item.Location)) continue;
            long size = item.Refusal is null ? FileSystemWalk.MeasureSize(item.Location, cancellationToken) : 0;
            items.Add(item with { SizeBytes = size });
        }

        return new RemovalPlan { Items = items, Note = plan.Note };
    }

    private static List<string> DiscoverFolders(CatalogApp target, CancellationToken cancellationToken)
    {
        var folders = new List<string>();
        foreach (string root in Roots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string child in Subfolders(root))
            {
                folders.Add(child);
                if (Descend(Path.GetFileName(child), target)) folders.AddRange(Subfolders(child));
            }
        }

        if (!string.IsNullOrWhiteSpace(target.InstallLocation)) folders.Add(target.InstallLocation);
        folders.AddRange(target.OwnedPaths);
        return folders;
    }

    private static IEnumerable<string> Roots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string[] roots =
        [
            local,
            roaming,
            Path.Join(profile, @"AppData\LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            documents,
            Path.Join(documents, "My Games"),
            Path.Join(profile, "Saved Games"),
            Path.Join(local, "Programs"),
            Path.Join(local, "Temp"),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        ];
        return roots.Where(root => root.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One more level inside a publisher folder or a known launcher library. Never inside Microsoft or Windows.</summary>
    private static bool Descend(string name, CatalogApp target)
    {
        if (name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Common Files", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Packages", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Equals("Epic Games", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("My Games", StringComparison.OrdinalIgnoreCase)) return true;
        return RemovalNames.PublisherMatches(name, target.Publisher);
    }

    private static List<string> Subfolders(string root)
    {
        try
        {
            return FileSystemWalk.List(root).Where(entry => entry.IsDirectory && !entry.IsLink).Select(entry => entry.Path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static List<ShortcutHint> DiscoverShortcuts()
    {
        var hints = new List<ShortcutHint>();
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        ];
        foreach (string root in roots.Where(root => root.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string link in Links(root, depth: 2))
                hints.Add(new ShortcutHint(link, ReadShortcutTarget(link)));
        }
        return hints;
    }

    private static IEnumerable<string> Links(string root, int depth)
    {
        List<FileSystemWalk.Entry> entries;
        try
        {
            entries = FileSystemWalk.List(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory && entry.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) yield return entry.Path;
            else if (entry.IsDirectory && !entry.IsLink && depth > 1)
            {
                foreach (string nested in Links(entry.Path, depth - 1)) yield return nested;
            }
        }
    }

    private static string? ReadShortcutTarget(string link)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            object shell = Activator.CreateInstance(type)!;
            object shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [link])!;
            object? target = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
            return target is string path && path.Length > 0 ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or TargetInvocationException
            or MissingMethodException or InvalidCastException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static List<string> DiscoverRegistryKeys(CatalogApp target)
    {
        var keys = new List<string>();
        AddSoftwareKeys(RegistryHive.CurrentUser, "HKCU", target, keys);
        AddSoftwareKeys(RegistryHive.LocalMachine, "HKLM", target, keys);
        if (!string.IsNullOrWhiteSpace(target.UninstallKey)) keys.Add(target.UninstallKey);
        return keys;
    }

    private static void AddSoftwareKeys(RegistryHive hive, string prefix, CatalogApp target, List<string> keys)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (string path in new[] { "SOFTWARE", @"SOFTWARE\WOW6432Node" })
            {
                using var software = root.OpenSubKey(path);
                if (software is null) continue;
                foreach (string name in software.GetSubKeyNames())
                {
                    keys.Add(prefix + "\\" + path + "\\" + name);
                    if (!Descend(name, target)) continue;
                    using var sub = software.OpenSubKey(name);
                    if (sub is null) continue;
                    foreach (string child in sub.GetSubKeyNames())
                        keys.Add(prefix + "\\" + path + "\\" + name + "\\" + child);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
