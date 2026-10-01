using System.Diagnostics;

namespace DiskSweeper.Core.Deletion;

public sealed record CloseReport(IReadOnlyList<LockingProcess> Closed, IReadOnlyList<LockingProcess> StillRunning, IReadOnlyList<string> Notes);

/// <summary>The "close the program that has it open" fix.</summary>
public static class AppCloser
{
    private static readonly TimeSpan PoliteWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Asks each program to close the way clicking its X button would. With <paramref name="force"/>, programs
    /// that ignore the request are ended (unsaved work in them is lost). Services are stopped. Critical Windows
    /// processes and DiskSweeper itself are never touched. File Explorer is restarted afterwards.
    /// </summary>
    public static CloseReport Close(IEnumerable<LockingProcess> lockers, bool force)
    {
        var closed = new List<LockingProcess>();
        var stillRunning = new List<LockingProcess>();
        var notes = new List<string>();
        bool restartExplorer = false;

        foreach (var locker in lockers)
        {
            if (locker.Kind == LockerKind.Critical || locker.IsThisApp)
            {
                notes.Add($"{locker.DisplayName} can't be closed: Windows needs it. Delete on restart instead.");
                stillRunning.Add(locker);
                continue;
            }

            using var process = OpenSame(locker);
            if (process is null)
            {
                closed.Add(locker); // Already gone.
                continue;
            }

            if (locker.ServiceName is { Length: > 0 } service)
            {
                RunQuietly("sc.exe", "stop", service);
                if (!process.WaitForExit((int)TimeSpan.FromSeconds(15).TotalMilliseconds) && force) TryKill(process);
            }
            else if (locker.Kind == LockerKind.Explorer)
            {
                TryKill(process);
                restartExplorer = true;
            }
            else
            {
                if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
                if (!process.WaitForExit((int)PoliteWait.TotalMilliseconds) && force) TryKill(process);
            }

            process.Refresh();
            if (process.HasExited) closed.Add(locker);
            else stillRunning.Add(locker);
        }

        if (restartExplorer)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                notes.Add("Explorer was closed. Press Ctrl+Shift+Esc, choose Run new task, and enter explorer.exe to bring it back.");
            }
        }

        foreach (var locker in stillRunning.Where(l => l.Kind != LockerKind.Critical && !l.IsThisApp))
        {
            notes.Add(force
                ? $"{locker.DisplayName} is still running (it may need administrator rights to close)."
                : $"{locker.DisplayName} didn't close. It may be asking to save work, or you can force it to close.");
        }
        return new CloseReport(closed, stillRunning, notes);
    }

    /// <summary>Opens the process only if it is the same one Restart Manager reported (process IDs get reused).</summary>
    private static Process? OpenSame(LockingProcess locker)
    {
        try
        {
            var process = Process.GetProcessById(locker.ProcessId);
            if (Math.Abs((process.StartTime.ToUniversalTime() - locker.StartTimeUtc).TotalSeconds) < 2) return process;
            process.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
            process.WaitForExit((int)PoliteWait.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void RunQuietly(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            process?.WaitForExit(15_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
