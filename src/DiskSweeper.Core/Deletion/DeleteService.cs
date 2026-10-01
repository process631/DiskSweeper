using System.Runtime.InteropServices;

namespace DiskSweeper.Core.Deletion;

/// <summary>
/// Deletes files and folders safely and explains every failure. Every request passes the protected-path
/// check first, links are removed without touching what they point to, and every attempt is logged.
/// When something won't go, the result says why and which fix applies:
/// <list type="number">
/// <item>Read-only flags, 260+ character paths and odd names are handled automatically by a permanent delete.</item>
/// <item>In use: <see cref="LockFinder"/> names the program, and <see cref="AppCloser"/> can close it.</item>
/// <item>Still locked: <see cref="ScheduleForRestart"/> deletes it at the next startup (admin).</item>
/// <item>Access denied: <see cref="TakeOwnershipAndDelete"/> fixes permissions, then deletes (admin).</item>
/// </list>
/// </summary>
public sealed class DeleteService(DeletionLog? log = null)
{
    private const int MaxFailuresKept = 200;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorDirNotEmpty = 145;

    public DeletionLog? Log { get; } = log;

    /// <summary>
    /// Deletes one file or folder. <paramref name="knownSize"/> (from a scan) saves measuring it again.
    /// With <paramref name="dryRun"/>, nothing is touched and the result says what would happen.
    /// </summary>
    public DeleteResult Delete(string path, DeleteMethod method, bool dryRun = false, long? knownSize = null, CancellationToken cancellationToken = default)
    {
        path = LongPath.Normalize(path);

        if (ProtectedPaths.WhyProtected(path) is string why)
            return Record(method, new DeleteResult { Path = path, Outcome = DeleteOutcome.Refused, Reason = BlockReason.Protected, Message = why });

        if (!FileSystemWalk.Exists(path))
            return Record(method, new DeleteResult { Path = path, Outcome = DeleteOutcome.AlreadyGone, Message = "It was already gone." });

        long size = knownSize ?? FileSystemWalk.MeasureSize(path, cancellationToken);
        if (dryRun)
            return Record(method, new DeleteResult { Path = path, Outcome = DeleteOutcome.WouldDelete, Bytes = size, Message = "Dry run: nothing was deleted." });

        var result = method == DeleteMethod.RecycleBin
            ? Recycle(path, size, cancellationToken)
            : DeletePermanently(path, cancellationToken);
        return Record(method, result);
    }

    /// <summary>Queues the item for deletion when Windows next starts. Use for files a driver or service won't let go of.</summary>
    public DeleteResult ScheduleForRestart(string path, CancellationToken cancellationToken = default)
    {
        path = LongPath.Normalize(path);
        if (ProtectedPaths.WhyProtected(path) is string why)
            return Record("Delete on restart", new DeleteResult { Path = path, Outcome = DeleteOutcome.Refused, Reason = BlockReason.Protected, Message = why });

        if (!Elevation.IsElevated)
        {
            return Record("Delete on restart", new DeleteResult
            {
                Path = path, Outcome = DeleteOutcome.Failed, Reason = BlockReason.AccessDenied,
                Message = "Deleting on restart needs administrator rights.",
            });
        }

        long size = FileSystemWalk.MeasureSize(path, cancellationToken);
        var failures = new List<FailedItem>();
        int queued = RestartDelete.Schedule(path, failures, cancellationToken);
        var result = failures.Count == 0
            ? new DeleteResult
            {
                Path = path, Outcome = DeleteOutcome.ScheduledForRestart, Bytes = size,
                Message = $"{queued:N0} item(s) will be deleted the next time Windows starts.",
            }
            : new DeleteResult
            {
                Path = path, Outcome = DeleteOutcome.Failed, Reason = BlockReason.Other, Bytes = 0,
                Message = $"Only {queued:N0} item(s) could be queued for restart.",
                FailedItems = failures.Take(MaxFailuresKept).ToList(), FailedCount = failures.Count,
            };
        return Record("Delete on restart", result);
    }

    /// <summary>Makes you the owner with full control of the item and everything inside, then deletes it.</summary>
    public DeleteResult TakeOwnershipAndDelete(string path, DeleteMethod method, CancellationToken cancellationToken = default)
    {
        path = LongPath.Normalize(path);
        if (ProtectedPaths.WhyProtected(path) is string why)
            return Record("Take ownership", new DeleteResult { Path = path, Outcome = DeleteOutcome.Refused, Reason = BlockReason.Protected, Message = why });

        if (!Elevation.IsElevated)
        {
            return Record("Take ownership", new DeleteResult
            {
                Path = path, Outcome = DeleteOutcome.Failed, Reason = BlockReason.AccessDenied,
                Message = "Taking ownership needs administrator rights.",
            });
        }

        var ownershipFailures = Ownership.Take(path, cancellationToken);
        var result = Delete(path, method, cancellationToken: cancellationToken);
        if (result.Succeeded || ownershipFailures.Count == 0) return result;

        return new DeleteResult
        {
            Path = result.Path, Outcome = result.Outcome, Reason = result.Reason, Bytes = result.Bytes, LockedBy = result.LockedBy,
            Message = $"{result.Message} Ownership couldn't be changed on {ownershipFailures.Count:N0} item(s).",
            FailedItems = result.FailedItems.Concat(ownershipFailures).Take(MaxFailuresKept).ToList(),
            FailedCount = result.FailedCount + ownershipFailures.Count,
        };
    }

    private DeleteResult Recycle(string path, long size, CancellationToken cancellationToken)
    {
        if (!RecycleBin.CanTake(path))
        {
            return new DeleteResult
            {
                Path = path, Outcome = DeleteOutcome.Failed, Reason = BlockReason.RecycleBinCantTakeIt,
                Message = "The Recycle Bin can't hold paths this long or names ending in a dot or space. Delete it permanently instead.",
            };
        }

        int code = RecycleBin.Send(path);
        if (!FileSystemWalk.Exists(path))
            return new DeleteResult { Path = path, Outcome = DeleteOutcome.Recycled, Bytes = size };

        long left = FileSystemWalk.MeasureSize(path, cancellationToken);
        var lockers = LockFinder.Find(FileSystemWalk.Walk(path, cancellationToken).Where(e => !e.IsDirectory).Select(e => e.Path).Take(5000));
        var reason = lockers.Count > 0 || code is ErrorSharingViolation or ErrorLockViolation ? BlockReason.InUse
            : code is 5 or 0x78 ? BlockReason.AccessDenied
            : BlockReason.RecycleBinFailed;

        string message = reason switch
        {
            BlockReason.InUse => InUseMessage(lockers),
            BlockReason.AccessDenied => AccessDeniedMessage(),
            _ when code == -1 => "Cancelled. Windows asked whether to delete it permanently because it doesn't fit in the Recycle Bin.",
            _ => $"The Recycle Bin couldn't take it (error 0x{code:X}). Try deleting it permanently.",
        };
        return new DeleteResult
        {
            Path = path, Outcome = DeleteOutcome.Failed, Reason = reason, Bytes = Math.Max(0, size - left),
            Message = message, LockedBy = lockers,
        };
    }

    private static DeleteResult DeletePermanently(string path, CancellationToken cancellationToken)
    {
        var failures = new List<FailedItem>();
        int failureCount = 0;
        long freed = 0;

        void Fail(string itemPath, Exception ex)
        {
            failureCount++;
            if (failures.Count < MaxFailuresKept) failures.Add(new FailedItem(itemPath, Classify(ex), ex.Message));
        }

        var attributes = FileSystemWalk.GetAttributes(path)!.Value;
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;

        if (!isDirectory)
        {
            long length = FileSystemWalk.MeasureSize(path, cancellationToken);
            if (TryDeleteFile(path, attributes, Fail)) freed = length;
        }
        else if ((attributes & FileAttributes.ReparsePoint) != 0 && FileSystemWalk.IsLink(path))
        {
            TryRemoveFolder(path, attributes, Fail, quietIfNotEmpty: false); // Removes only the link, never its target.
        }
        else
        {
            // Delete files on the way down, remember folders, then remove the folders deepest first.
            var folders = new List<(string Path, FileAttributes Attributes)> { (path, attributes) };
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string folder = stack.Pop();
                List<FileSystemWalk.Entry> entries;
                try
                {
                    entries = FileSystemWalk.List(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Fail(folder, ex);
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (!entry.IsDirectory)
                    {
                        if (TryDeleteFile(entry.Path, entry.Attributes, Fail) && (entry.Attributes & FileSystemWalk.CloudOnlyAttributes) == 0)
                            freed += entry.Length;
                    }
                    else if (entry.IsLink)
                    {
                        TryRemoveFolder(entry.Path, entry.Attributes, Fail, quietIfNotEmpty: false);
                    }
                    else
                    {
                        folders.Add((entry.Path, entry.Attributes));
                        stack.Push(entry.Path);
                    }
                }
            }

            for (int i = folders.Count - 1; i >= 0; i--)
                TryRemoveFolder(folders[i].Path, folders[i].Attributes, Fail, quietIfNotEmpty: true);
        }

        if (!FileSystemWalk.Exists(path))
            return new DeleteResult { Path = path, Outcome = DeleteOutcome.Deleted, Bytes = freed };

        // Ask Restart Manager who holds the files that refused. "Access denied" is also what Windows
        // reports for a running program's own .exe, so those get checked too.
        var suspects = failures
            .Where(f => f.Reason is BlockReason.InUse or BlockReason.AccessDenied && File.Exists(LongPath.ToExtended(f.Path)))
            .Select(f => f.Path);
        var lockers = LockFinder.Find(suspects);

        var reason = lockers.Count > 0 || failures.Any(f => f.Reason == BlockReason.InUse) ? BlockReason.InUse
            : failures.Any(f => f.Reason == BlockReason.AccessDenied) ? BlockReason.AccessDenied
            : BlockReason.Other;

        string message = reason switch
        {
            BlockReason.InUse => InUseMessage(lockers),
            BlockReason.AccessDenied => AccessDeniedMessage(),
            _ => failures.FirstOrDefault()?.Message ?? "Something inside couldn't be deleted.",
        };
        if (freed > 0) message = $"Freed {ByteSize.Format(freed)}, but {failureCount:N0} item(s) couldn't be deleted. {message}";

        return new DeleteResult
        {
            Path = path, Outcome = DeleteOutcome.Failed, Reason = reason, Bytes = freed, Message = message,
            FailedItems = failures, FailedCount = failureCount, LockedBy = lockers,
        };
    }

    private static bool TryDeleteFile(string path, FileAttributes attributes, Action<string, Exception> fail)
    {
        string extended = LongPath.ToExtended(path);
        try
        {
            // Read-only files refuse deletion; hidden and system flags don't matter.
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(extended, attributes & ~FileAttributes.ReadOnly);
            File.Delete(extended);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fail(path, ex);
            return false;
        }
    }

    private static void TryRemoveFolder(string path, FileAttributes attributes, Action<string, Exception> fail, bool quietIfNotEmpty)
    {
        string extended = LongPath.ToExtended(path);
        try
        {
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(extended, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(extended, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException ex) when (quietIfNotEmpty && (ex.HResult & 0xFFFF) == ErrorDirNotEmpty)
        {
            // Something inside failed and has already been reported.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fail(path, ex);
        }
    }

    private static BlockReason Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => BlockReason.AccessDenied,
        IOException io when (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation => BlockReason.InUse,
        _ => BlockReason.Other,
    };

    private static string InUseMessage(IReadOnlyList<LockingProcess> lockers) => lockers.Count switch
    {
        0 => "A program has it open. Close programs that might be using it (or a terminal/Explorer window open in that folder), or delete it on the next restart.",
        _ => $"In use by {string.Join(", ", lockers.Select(l => l.ToString()))}. Close it and try again, or delete it on the next restart.",
    };

    private static string AccessDeniedMessage() => Elevation.IsElevated
        ? "Windows denied permission. Use “Take ownership” to fix its permissions and delete it."
        : "Windows denied permission. Restart DiskSweeper as administrator, then use “Take ownership”.";

    private DeleteResult Record(DeleteMethod method, DeleteResult result) =>
        Record(method == DeleteMethod.RecycleBin ? "Recycle" : "Delete", result);

    private DeleteResult Record(string action, DeleteResult result)
    {
        try
        {
            Log?.Write(action, result.Outcome.ToString(), result.Bytes, result.Path, result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            // A log that can't be written must never stop a delete from being reported.
        }
        return result;
    }
}
