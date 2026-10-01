using System.Diagnostics;
using Microsoft.Win32;

namespace DiskSweeper.Core.SmartFinds;

/// <summary>
/// Bulk Crap Uninstaller (open source, Apache 2.0) is the best tool for removing programs together with
/// their registry entries, services and scheduled tasks. DiskSweeper finds leftover files; BCU handles
/// "uninstall it properly", so the app offers to open it rather than re-inventing it.
/// </summary>
public static class BulkCrapUninstaller
{
    public const string DownloadPage = "https://github.com/Klocman/Bulk-Crap-Uninstaller/releases";

    public static string? FindExecutable()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string keyPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var key = hive.OpenSubKey(keyPath);
                foreach (string name in key?.GetSubKeyNames() ?? [])
                {
                    using var entry = key!.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || !display.Contains("Bulk Crap Uninstaller", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.GetValue("InstallLocation") is string location && File.Exists(Path.Join(location, "BCUninstaller.exe")))
                        return Path.Join(location, "BCUninstaller.exe");
                }
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate = Path.Join(programFiles, @"BCUninstaller\BCUninstaller.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Opens BCU if it's installed, otherwise its download page. Returns true if BCU itself was started.</summary>
    public static bool Open()
    {
        string? exe = FindExecutable();
        Process.Start(new ProcessStartInfo(exe ?? DownloadPage) { UseShellExecute = true });
        return exe is not null;
    }
}
