using System.Diagnostics;

namespace DiskSweeper.Core.Programs;

/// <summary>Decides which running programs would get in the way of removing one app. Matching is exact, never "contains Epic".</summary>
public static class RunningPrograms
{
    public sealed record Snapshot(int ProcessId, DateTime StartTimeUtc, string ProcessName, string? ExecutablePath);

    private static readonly HashSet<string> NeverClose = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "smss", "dwm",
        "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "textinputhost", "system", "registry", "idle", "disksweeper", "dsweep",
        "applicationframehost", "systemsettings", "securityhealthservice", "msmpeng",
    };

    private static readonly string[] SteamFamily = ["steam", "steamwebhelper", "steamservice", "steamerrorreporter"];

    private static readonly string[] EpicLauncher = ["EpicGamesLauncher", "EpicWebHelper"];

    private static readonly string[] GogLauncher = ["GalaxyClient", "GalaxyClient Helper"];

    /// <summary>Processes that are this program, or its launcher, and are not the uninstaller we are about to start.</summary>
    public static IReadOnlyList<Snapshot> Blocking(
        IReadOnlyList<Snapshot> processes,
        CatalogApp app,
        IReadOnlyList<string> folders,
        UninstallLaunch? uninstall)
    {
        var hits = new List<Snapshot>();
        foreach (var process in processes)
        {
            if (process.ProcessId == Environment.ProcessId) continue;
            if (NeverClose.Contains(process.ProcessName)) continue;
            if (IsUninstaller(process, uninstall)) continue;
            if (uninstall?.Style == UninstallStyle.LauncherHandoff && IsSteamFamily(process.ProcessName)) continue;
            if (RunsFrom(process, folders) || IsLauncher(app, process.ProcessName)) hits.Add(process);
        }
        return hits;
    }

    public static List<Snapshot> Capture()
    {
        var list = new List<Snapshot>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                DateTime started;
                try
                {
                    started = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    continue;
                }

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                }
                list.Add(new Snapshot(process.Id, started, process.ProcessName, path));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return list;
    }

    private static bool RunsFrom(Snapshot process, IReadOnlyList<string> folders)
    {
        if (process.ExecutablePath is not string path) return false;
        foreach (string folder in folders)
        {
            if (LongPath.IsSameOrInside(path, folder)) return true;
        }
        return false;
    }

    private static bool IsLauncher(CatalogApp app, string processName)
    {
        if (app.Source == AppSource.Epic && EpicLauncher.Contains(processName, StringComparer.OrdinalIgnoreCase)) return true;
        if (app.Source == AppSource.Gog && GogLauncher.Contains(processName, StringComparer.OrdinalIgnoreCase)) return true;
        if (app.Source != AppSource.Roblox) return false;
        bool studio = app.Name.Contains("Studio", StringComparison.OrdinalIgnoreCase);
        if (studio)
            return processName.Equals("RobloxStudioBeta", StringComparison.OrdinalIgnoreCase)
                || processName.Equals("RobloxStudioLauncher", StringComparison.OrdinalIgnoreCase);
        return processName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("RobloxPlayerLauncher", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("RobloxCrashHandler", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUninstaller(Snapshot process, UninstallLaunch? uninstall)
    {
        if (uninstall is null) return false;
        string wanted = Path.GetFileNameWithoutExtension(uninstall.FileName);
        if (process.ProcessName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return process.ExecutablePath is string path
            && path.Equals(uninstall.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSteamFamily(string processName) =>
        SteamFamily.Contains(processName, StringComparer.OrdinalIgnoreCase);
}
