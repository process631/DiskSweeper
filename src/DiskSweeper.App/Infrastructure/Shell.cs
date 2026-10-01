using System.Diagnostics;
using System.IO;
using System.Windows;
using DiskSweeper.Core;

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
    /// Opens a command the user chose to run from a Cleanup advice (e.g. a Settings page or
    /// System Properties). Commands that need a terminal open in an administrator PowerShell window.
    /// </summary>
    public static void RunAdvice(string command)
    {
        if (command.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrl(command[6..].Trim());
        }
        else if (command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -Command \"{command}\"") { UseShellExecute = true, Verb = "runas" });
        }
    }
}
