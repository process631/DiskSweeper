using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DiskSweeper.Core.SmartFinds;

public sealed record InstalledProgram(string DisplayName, string? Publisher, string? InstallLocation, IReadOnlyList<string> RelatedFolders);

/// <summary>Everything that says "this software is still installed or running", gathered from Windows.</summary>
public sealed partial class InstalledSoftware
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public required IReadOnlyList<InstalledProgram> Programs { get; init; }

    /// <summary>Folders containing a service's or driver's program file (anti-cheat, updaters, VPN clients…).</summary>
    public required IReadOnlyList<string> ServiceFolders { get; init; }

    /// <summary>Registered service and driver names, e.g. "EasyAntiCheat_EOS".</summary>
    public required IReadOnlyList<string> ServiceNames { get; init; }

    /// <summary>Folders on the PATH variable: command-line tools installed without an uninstaller still live there.</summary>
    public required IReadOnlyList<string> PathFolders { get; init; }

    /// <summary>Folders of programs running right now.</summary>
    public required IReadOnlyList<string> RunningFolders { get; init; }

    /// <summary>Package family names of installed Microsoft Store apps, e.g. "Microsoft.Paint_8wekyb3d8bbwe".</summary>
    public required IReadOnlySet<string> StorePackageFamilies { get; init; }

    public static InstalledSoftware Load()
    {
        var (serviceNames, serviceFolders) = LoadServices();
        return new InstalledSoftware
        {
            Programs = LoadPrograms(),
            ServiceFolders = serviceFolders,
            ServiceNames = serviceNames,
            PathFolders = LoadPathFolders(),
            RunningFolders = LoadRunningFolders(),
            StorePackageFamilies = LoadStorePackageFamilies(),
        };
    }

    private static List<InstalledProgram> LoadPrograms()
    {
        var programs = new List<InstalledProgram>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string keyPath in UninstallKeys)
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key is null) continue;
                foreach (string name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string displayName || displayName.Length == 0) continue;

                    string? location = Clean(entry.GetValue("InstallLocation") as string);
                    var related = new[] { location, FolderOfCommand(entry.GetValue("DisplayIcon") as string), FolderOfCommand(entry.GetValue("UninstallString") as string) }
                        .OfType<string>()
                        .Where(IsUsefulFolder)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    programs.Add(new InstalledProgram(displayName, entry.GetValue("Publisher") as string, location, related));
                }
            }
        }
        return programs;
    }

    private static (List<string> Names, List<string> Folders) LoadServices()
    {
        var names = new List<string>();
        var folders = new List<string>();
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (string name in services?.GetSubKeyNames() ?? [])
        {
            names.Add(name);
            using var service = services!.OpenSubKey(name);
            if (service?.GetValue("ImagePath") is string image && FolderOfCommand(image) is string folder && IsUsefulFolder(folder))
                folders.Add(folder);
        }
        return (names, folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<string> LoadPathFolders()
    {
        var folders = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            string path = Environment.GetEnvironmentVariable("PATH", target) ?? "";
            foreach (string entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Clean(Environment.ExpandEnvironmentVariables(entry)) is string folder && IsUsefulFolder(folder)) folders.Add(folder);
            }
        }
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> LoadRunningFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is string file && Path.GetDirectoryName(file) is string folder) folders.Add(folder);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // System and protected processes can't be inspected; they don't live in leftover folders anyway.
            }
            finally
            {
                process.Dispose();
            }
        }
        return folders.ToList();
    }

    private static HashSet<string> LoadStorePackageFamilies()
    {
        // Each installed package for this user has a key named after its full name:
        // Name_Version_Architecture_ResourceId_PublisherId. The family name is Name_PublisherId.
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        foreach (string fullName in key?.GetSubKeyNames() ?? [])
        {
            string[] parts = fullName.Split('_');
            if (parts.Length >= 5) families.Add($"{parts[0]}_{parts[^1]}");
        }
        return families;
    }

    /// <summary>Extracts the folder from things like <c>"C:\Program Files\App\app.exe",0</c> or <c>MsiExec.exe /X{...}</c>.</summary>
    internal static string? FolderOfCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());

        string path;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end < 0) return null;
            path = command[1..end];
        }
        else
        {
            var match = ExecutablePath().Match(command);
            if (!match.Success) return null;
            path = match.Value;
        }

        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[12..]);
        if (!Path.IsPathFullyQualified(path)) return null;
        return Clean(Path.GetDirectoryName(path));
    }

    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"').Trim();
        try
        {
            return Path.IsPathFullyQualified(path) ? LongPath.Normalize(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Paths like C:\Windows\System32 tell us nothing about which program folders are in use.</summary>
    private static bool IsUsefulFolder(string folder)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !LongPath.IsSameOrInside(folder, windows) && Path.GetDirectoryName(folder) is not null;
    }

    [GeneratedRegex(@"^[A-Za-z]:\\[^""]*?\.(exe|dll|sys|ico|com|bat|cmd)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutablePath();
}
