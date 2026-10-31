using System.Diagnostics;
using System.IO;
using System.Windows;
using DiskSweeper.Core;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.App.Infrastructure;

/// <summary>Small hand-offs to Windows: show in Explorer, copy a path, open a link or a command.</summary>
public static class Shell
{
    public static void ShowInExplorer(string path)
    {
        string target = LongPath.ToNormal(path);
        if (FileSystemWalk.Exists(target))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        else if (Path.GetDirectoryName(target) is string parent && Directory.Exists(parent))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
    }

    public static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another app is holding the clipboard; trying again usually works.
        }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>
    /// Opens a command the user chose to run from a Cleanup advice (a Settings page or System Properties).
    /// Commands that need a terminal open in an administrator PowerShell window. Returns false when the
    /// command is not one of the shapes DiskSweeper ships (so a changed rules file can't launch an arbitrary program).
    /// </summary>
    public static bool RunAdvice(string command)
    {
        if (!AdviceCommands.IsAllowed(command)) return false;

        if (command.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrl(command[6..].Trim());
            return true;
        }

        if (command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
            return true;
        }

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            ArgumentList = { "-NoExit", "-NoProfile", "-Command", command },
        });
        return true;
    }
}
