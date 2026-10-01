using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskSweeper.Core.Deletion;

/// <summary>
/// The last-resort fix for files a driver or service keeps open: Windows deletes them early in the
/// next startup, before anything can lock them. Needs administrator rights.
/// </summary>
public static class RestartDelete
{
    private const uint MoveFileDelayUntilReboot = 0x4;

    /// <summary>Queues <paramref name="path"/> and everything inside it for deletion at the next restart. Returns how many items were queued.</summary>
    public static int Schedule(string path, List<FailedItem> failures, CancellationToken cancellationToken = default)
    {
        if (!Elevation.IsElevated) throw new UnauthorizedAccessException("Deleting on restart needs administrator rights.");

        // Windows can only remove empty folders at startup, and runs the queue in order:
        // so queue every file first, then folders from the deepest up.
        var entries = FileSystemWalk.Walk(path, cancellationToken).ToList();
        int queued = 0;
        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0) TryClearReadOnly(entry.Path, entry.Attributes);
            queued += Queue(entry.Path, failures) ? 1 : 0;
        }
        foreach (var entry in entries.Where(e => e.IsDirectory).Reverse())
        {
            queued += Queue(entry.Path, failures) ? 1 : 0;
        }
        return queued;
    }

    private static bool Queue(string path, List<FailedItem> failures)
    {
        if (MoveFileEx(LongPath.ToExtended(path), null, MoveFileDelayUntilReboot)) return true;
        failures.Add(new FailedItem(path, BlockReason.Other, new Win32Exception(Marshal.GetLastWin32Error()).Message));
        return false;
    }

    private static void TryClearReadOnly(string path, FileAttributes attributes)
    {
        try
        {
            File.SetAttributes(LongPath.ToExtended(path), attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
