using System.IO.Enumeration;

namespace DiskSweeper.Core;

/// <summary>Low-level folder listing shared by deleting, measuring and permission fixing. Never follows links.</summary>
public static class FileSystemWalk
{
    // Set on OneDrive-style placeholders whose contents live in the cloud, not on this PC.
    internal const FileAttributes CloudOnlyAttributes = (FileAttributes)0x0040_0000 | FileAttributes.Offline;

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public readonly record struct Entry(string Path, bool IsDirectory, bool IsLink, long Length, FileAttributes Attributes, DateTime LastWriteTimeUtc);

    /// <summary>Lists one folder. Throws <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/> if it can't be read.</summary>
    public static List<Entry> List(string folderPath)
    {
        var raw = new FileSystemEnumerable<(string Name, bool IsDirectory, long Length, FileAttributes Attributes, DateTime Time)>(
            LongPath.ToExtended(folderPath),
            (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory, entry.IsDirectory ? 0 : entry.Length,
                entry.Attributes, entry.LastWriteTimeUtc.UtcDateTime),
            OneLevel);

        var entries = new List<Entry>();
        foreach (var (name, isDirectory, length, attributes, time) in raw)
        {
            string path = System.IO.Path.Join(folderPath, name);
            bool isLink = isDirectory && (attributes & FileAttributes.ReparsePoint) != 0 && IsLink(path);
            entries.Add(new Entry(path, isDirectory, isLink, length, attributes, time));
        }
        return entries;
    }

    /// <summary>
    /// True for junctions and symbolic links. Other reparse points, such as OneDrive folders, are real folders.
    /// If we can't tell, we play it safe and treat it as a link (so it's never followed).
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(LongPath.ToExtended(path)).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Attributes of a file or folder, or null if it doesn't exist.</summary>
    public static FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(LongPath.ToExtended(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static bool Exists(string path) => GetAttributes(path) is not null;

    /// <summary>Bytes a file or folder uses on this PC. Unreadable parts are skipped.</summary>
    public static long MeasureSize(string path, CancellationToken cancellationToken = default) =>
        Walk(path, cancellationToken).Where(e => !e.IsDirectory && (e.Attributes & CloudOnlyAttributes) == 0).Sum(e => e.Length);

    /// <summary>
    /// Every file and folder under <paramref name="path"/>, parents before children. Links are returned but not entered.
    /// Folders that can't be listed are skipped silently.
    /// </summary>
    public static IEnumerable<Entry> Walk(string path, CancellationToken cancellationToken = default)
    {
        var attributes = GetAttributes(path);
        if (attributes is null) yield break;

        bool isDirectory = (attributes.Value & FileAttributes.Directory) != 0;
        if (!isDirectory)
        {
            var info = new FileInfo(LongPath.ToExtended(path));
            long length = 0;
            DateTime time = default;
            try
            {
                length = info.Length;
                time = info.LastWriteTimeUtc;
            }
            catch (IOException)
            {
            }
            yield return new Entry(path, false, false, length, attributes.Value, time);
            yield break;
        }

        bool rootIsLink = (attributes.Value & FileAttributes.ReparsePoint) != 0 && IsLink(path);
        yield return new Entry(path, true, rootIsLink, 0, attributes.Value, Directory.GetLastWriteTimeUtc(LongPath.ToExtended(path)));
        if (rootIsLink) yield break;

        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Entry> entries;
            try
            {
                entries = List(stack.Pop());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                yield return entry;
                if (entry.IsDirectory && !entry.IsLink) stack.Push(entry.Path);
            }
        }
    }
}
