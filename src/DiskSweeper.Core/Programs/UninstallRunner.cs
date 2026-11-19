using System.ComponentModel;
using System.Diagnostics;

namespace DiskSweeper.Core.Programs;

public sealed record UninstallOutcome(int? ExitCode, bool Started, bool Succeeded, string Message);

/// <summary>
/// Starts a program's own uninstaller and waits for it. The window is hidden only for uninstallers
/// that are actually silent. UseShellExecute lets the uninstaller ask Windows for administrator rights itself.
/// </summary>
public static class UninstallRunner
{
    public static async Task<UninstallOutcome> RunAsync(UninstallLaunch launch, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = true,
            WindowStyle = launch.Style == UninstallStyle.Silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };
        foreach (string argument in launch.Arguments) start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UninstallOutcome(null, false, false, "Cancelled at the Windows permission prompt.");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new UninstallOutcome(null, false, false, ex.Message);
        }

        if (process is null) return new UninstallOutcome(null, false, false, "Windows couldn't start the uninstaller.");

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
            int code = process.ExitCode;
            bool ok = code is 0 or 3010 or 1641;
            string message = ok
                ? launch.Style == UninstallStyle.LauncherHandoff
                    ? "The launcher was asked to uninstall it. Finish that there if a window opened, then leftovers that are still on disk can be removed."
                    : code == 0 ? "The uninstaller finished." : "The uninstaller finished. Windows may ask you to restart."
                : $"The uninstaller returned code {code}. It may have been cancelled.";
            return new UninstallOutcome(code, true, ok, message);
        }
    }
}
