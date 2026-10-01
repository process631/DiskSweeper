using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace DiskSweeper.Core.Scanning;

/// <summary>
/// Walks a folder tree in parallel and builds a <see cref="FileNode"/> tree with sizes.
/// Junctions and symbolic links are recorded but never followed, so nothing is counted twice
/// and the scan can't wander onto other drives or loop forever.
/// </summary>
public sealed class DiskScanner
{
    private static readonly EnumerationOptions ListOneFolder = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,   // We want to hear about folders we can't read, not silently skip them.
        AttributesToSkip = 0,         // The default skips hidden and system files, which would hide pagefile.sys and friends.
        ReturnSpecialDirectories = false,
    };

    private readonly ScanOptions _options;

    public DiskScanner(ScanOptions? options = null) => _options = options ?? new ScanOptions();

    /// <summary>Scans a drive or folder. Throws <see cref="DirectoryNotFoundException"/> right away if it doesn't exist.</summary>
    public Task<ScanResult> ScanAsync(string path, ScanProgress? progress = null, CancellationToken cancellationToken = default)
    {
        string rootPath = LongPath.Normalize(path);
        if (!Directory.Exists(LongPath.ToExtended(rootPath))) throw new DirectoryNotFoundException($"Folder not found: {path}");

        if (_options.UseBackupPrivilege && Elevation.IsElevated) Privileges.TryEnable(Privileges.Backup);
        return ScanCoreAsync(rootPath, progress ?? new ScanProgress(), cancellationToken);
    }

    private async Task<ScanResult> ScanCoreAsync(string rootPath, ScanProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var rootInfo = new DirectoryInfo(LongPath.ToExtended(rootPath));
        var root = new FileNode(rootPath, null, NodeFlags.Directory, 0, rootInfo.LastWriteTimeUtc, rootInfo.Attributes);
        var state = new ScanState(progress);

        // Work queue of folders still to read. `pending` counts folders queued or being read;
        // when it drops to zero the whole tree is done and every worker is woken up to exit.
        var queue = new ConcurrentQueue<(FileNode Folder, string Path)>();
        using var available = new SemaphoreSlim(0);
        int pending = 1;
        queue.Enqueue((root, rootPath));
        available.Release();

        int workerCount = Math.Max(1, _options.MaxParallelism);
        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    available.Wait(cancellationToken);
                    if (!queue.TryDequeue(out var item)) return;

                    try
                    {
                        foreach (var subfolder in ReadFolder(item.Folder, item.Path, state, cancellationToken))
                        {
                            Interlocked.Increment(ref pending);
                            queue.Enqueue(subfolder);
                            available.Release();
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0) available.Release(workerCount);
                    }
                }
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        TreeTotals.Compute(root);

        var unreadable = state.Unreadable.ToList();
        unreadable.Sort(StringComparer.OrdinalIgnoreCase);
        return new ScanResult(root, startedAt, stopwatch.Elapsed, ScanMethod.FolderWalk, unreadable,
            state.Links, state.CloudOnlyFiles, state.CloudOnlyBytes);
    }

    /// <summary>Lists one folder, attaches its contents to the tree, and returns the subfolders that still need reading.</summary>
    private static List<(FileNode, string)> ReadFolder(FileNode folder, string folderPath, ScanState state, CancellationToken cancellationToken)
    {
        var children = new List<FileNode>();
        var subfolders = new List<(FileNode, string)>();
        long fileBytes = 0;
        long fileCount = 0;

        try
        {
            var entries = new FileSystemEnumerable<FileNode>(
                LongPath.ToExtended(folderPath),
                (ref FileSystemEntry entry) => new FileNode(
                    entry.FileName.ToString(),
                    folder,
                    entry.IsDirectory ? NodeFlags.Directory : NodeFlags.None,
                    entry.IsDirectory ? 0 : entry.Length,
                    entry.LastWriteTimeUtc.UtcDateTime,
                    entry.Attributes),
                ListOneFolder);

            foreach (var node in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                children.Add(node);

                if (node.IsDirectory)
                {
                    string childPath = Path.Join(folderPath, node.Name);
                    if ((node.Attributes & FileAttributes.ReparsePoint) != 0 && FileSystemWalk.IsLink(childPath))
                    {
                        node.Flags |= NodeFlags.Link;
                        Interlocked.Increment(ref state.Links);
                    }
                    else
                    {
                        subfolders.Add((node, childPath));
                    }
                }
                else if ((node.Attributes & FileSystemWalk.CloudOnlyAttributes) != 0)
                {
                    node.Flags |= NodeFlags.CloudOnly;
                    Interlocked.Increment(ref state.CloudOnlyFiles);
                    Interlocked.Add(ref state.CloudOnlyBytes, node.Size);
                    node.Size = 0;
                    fileCount++;
                }
                else
                {
                    fileBytes += node.Size;
                    fileCount++;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Keep whatever was listed before the error; the folder is marked so its total is known to be incomplete.
            folder.Flags |= NodeFlags.Unreadable;
            state.Unreadable.Add(folderPath);
        }

        folder.ChildList = children;
        folder.Size = fileBytes;
        folder.FileCount = fileCount;
        state.Progress.Add(fileCount, 1, fileBytes);
        return subfolders;
    }

    private sealed class ScanState(ScanProgress progress)
    {
        public readonly ScanProgress Progress = progress;
        public readonly ConcurrentBag<string> Unreadable = [];
        public long Links;
        public long CloudOnlyFiles;
        public long CloudOnlyBytes;
    }
}
