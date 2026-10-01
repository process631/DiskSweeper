using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Core.Analysis;

public sealed record SavedScanInfo(string File, string RootPath, DateTime ScannedAtUtc, long TotalSize, long FileCount);

/// <summary>
/// Saves scans to compact compressed files so the app can show the last scan instantly on startup and
/// compare against it later ("what grew since last week"). Keeps the newest few per scanned folder.
/// Stored in %LOCALAPPDATA%\DiskSweeper\scans.
/// </summary>
public static class ScanStore
{
    private const string Magic = "DSSCAN";
    private const int Version = 1;
    private const int KeepPerRoot = 5;

    public static string Folder { get; set; } = Path.Join(ProtectedPaths.AppDataFolder, "scans");

    public static string Save(ScanResult result)
    {
        string rootFolder = Path.Join(Folder, KeyFor(result.Root.Name));
        Directory.CreateDirectory(rootFolder);
        string file = Path.Join(rootFolder, $"{result.ScannedAtUtc:yyyyMMdd-HHmmss}.dsscan");
        string temp = file + ".tmp";

        using (var stream = File.Create(temp))
        using (var zip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(zip, Encoding.UTF8))
        {
            WriteHeader(writer, result);
            foreach (var node in result.EnumerateNodes())
            {
                writer.Write(node.Name);
                writer.Write((byte)node.Flags);
                writer.Write7BitEncodedInt64(node.Size);
                writer.Write(node.LastWriteTimeUtc.Ticks);
                writer.Write((int)node.Attributes);
                writer.Write7BitEncodedInt64(node.FileCount);
                writer.Write7BitEncodedInt64(node.FolderCount);
                writer.Write7BitEncodedInt(node.ChildList?.Count ?? -1);
            }
        }
        File.Move(temp, file, overwrite: true);

        foreach (var old in List(result.Root.Name).Skip(KeepPerRoot)) TryDelete(old.File);
        return file;
    }

    public static ScanResult Load(string file)
    {
        using var stream = File.OpenRead(file);
        using var zip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(zip, Encoding.UTF8);

        var header = ReadHeader(reader, file);
        int unreadableCount = reader.ReadInt32();
        var unreadable = new List<string>(unreadableCount);
        for (int i = 0; i < unreadableCount; i++) unreadable.Add(reader.ReadString());

        // Nodes were written parents-first; each folder is followed by its children's subtrees in order.
        FileNode ReadNode(FileNode? parent, out int childCount)
        {
            string name = reader.ReadString();
            var flags = (NodeFlags)reader.ReadByte();
            long size = reader.Read7BitEncodedInt64();
            var time = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var attributes = (FileAttributes)reader.ReadInt32();
            var node = new FileNode(name, parent, flags, size, time, attributes)
            {
                FileCount = reader.Read7BitEncodedInt64(),
                FolderCount = reader.Read7BitEncodedInt64(),
            };
            childCount = reader.Read7BitEncodedInt();
            if (childCount >= 0) node.ChildList = new List<FileNode>(childCount);
            return node;
        }

        var root = ReadNode(null, out int rootChildren);
        var stack = new Stack<(FileNode Folder, int Remaining)>();
        if (rootChildren > 0) stack.Push((root, rootChildren));
        while (stack.Count > 0)
        {
            var (folder, remaining) = stack.Pop();
            var child = ReadNode(folder, out int childCount);
            folder.ChildList!.Add(child);
            if (remaining > 1) stack.Push((folder, remaining - 1));
            if (childCount > 0) stack.Push((child, childCount));
        }

        return new ScanResult(root, header.ScannedAtUtc, TimeSpan.Zero, ScanMethod.Saved, unreadable, header.Links, header.CloudFiles, header.CloudBytes);
    }

    /// <summary>Saved scans, newest first; only those of <paramref name="rootPath"/> when given.</summary>
    public static IReadOnlyList<SavedScanInfo> List(string? rootPath = null)
    {
        string folder = rootPath is null ? Folder : Path.Join(Folder, KeyFor(LongPath.Normalize(rootPath)));
        if (!Directory.Exists(folder)) return [];

        var scans = new List<SavedScanInfo>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.dsscan", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var zip = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new BinaryReader(zip, Encoding.UTF8);
                var header = ReadHeader(reader, file);
                scans.Add(new SavedScanInfo(file, header.RootPath, header.ScannedAtUtc, header.TotalSize, header.FileCount));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }
        return scans.OrderByDescending(s => s.ScannedAtUtc).ToList();
    }

    private static void WriteHeader(BinaryWriter writer, ScanResult result)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(result.Root.Name);
        writer.Write(result.ScannedAtUtc.Ticks);
        writer.Write(result.Root.Size);
        writer.Write(result.Root.FileCount);
        writer.Write(result.LinkCount);
        writer.Write(result.CloudOnlyFileCount);
        writer.Write(result.CloudOnlyBytes);
        writer.Write(result.UnreadableFolders.Count);
        foreach (string folder in result.UnreadableFolders) writer.Write(folder);
    }

    private static (string RootPath, DateTime ScannedAtUtc, long TotalSize, long FileCount, long Links, long CloudFiles, long CloudBytes) ReadHeader(BinaryReader reader, string file)
    {
        if (reader.ReadString() != Magic || reader.ReadInt32() != Version)
            throw new InvalidDataException($"{file} isn't a DiskSweeper scan from this version.");
        return (reader.ReadString(), new DateTime(reader.ReadInt64(), DateTimeKind.Utc), reader.ReadInt64(), reader.ReadInt64(),
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
    }

    /// <summary>A short, file-name-safe key per scanned folder: readable prefix plus a hash.</summary>
    private static string KeyFor(string rootPath)
    {
        string readable = new(rootPath.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.ToUpperInvariant())))[..10];
        return $"{readable.Trim('_')[..Math.Min(40, readable.Trim('_').Length)]}-{hash}";
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
