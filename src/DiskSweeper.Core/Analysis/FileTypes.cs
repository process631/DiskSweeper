using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Core.Analysis;

public enum FileKind
{
    Video,
    Audio,
    Images,
    Archives,
    DiskImages,
    Programs,
    GameData,
    Documents,
    Code,
    System,
    Other,
}

public sealed record FileTypeStats(string Extension, FileKind Kind, long Size, long Count);

public sealed record FileKindStats(FileKind Kind, long Size, long Count);

/// <summary>"What kind of stuff fills my disk": totals per file extension and per broad kind.</summary>
public static class FileTypes
{
    private static readonly Dictionary<string, FileKind> Kinds = Build(new()
    {
        [FileKind.Video] = ".mp4 .mkv .avi .mov .wmv .webm .m4v .flv .mpg .mpeg .ts .m2ts",
        [FileKind.Audio] = ".mp3 .wav .flac .aac .ogg .m4a .wma .opus .aiff",
        [FileKind.Images] = ".jpg .jpeg .png .gif .bmp .webp .heic .heif .tif .tiff .raw .cr2 .nef .arw .dng .psd .svg .ico",
        [FileKind.Archives] = ".zip .7z .rar .tar .gz .tgz .xz .bz2 .zst .cab",
        [FileKind.DiskImages] = ".iso .img .vhd .vhdx .vmdk .vdi .ova .ovf .qcow2 .vmem .vmsn .wim .esd .dmg",
        [FileKind.Programs] = ".exe .msi .msp .dll .appx .msix .appxbundle .msixbundle .jar .node",
        [FileKind.GameData] = ".pak .ucas .utoc .uasset .umap .bundle .assets .resource .vpk .bsa .ba2 .wad .forge .rpf .sav .nvph .unity3d .gma .bik",
        [FileKind.Documents] = ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .odt .ods .rtf .csv .epub .one",
        [FileKind.Code] = ".cs .js .mjs .ts .tsx .py .pyc .java .class .cpp .c .h .hpp .go .rs .json .xml .html .css .ipynb .o .obj .pdb .lib .a .so",
        [FileKind.System] = ".sys .dmp .etl .evtx .log .tmp .cache .db .dat .edb .mui .cat .manifest .regtrans-ms .blf",
    });

    /// <summary>Name for people: "Disk images" rather than "DiskImages".</summary>
    public static string DisplayName(FileKind kind) => kind switch
    {
        FileKind.DiskImages => "Disk images",
        FileKind.GameData => "Game data",
        _ => kind.ToString(),
    };

    public static FileKind KindOf(string extension) =>
        Kinds.TryGetValue(extension.ToLowerInvariant(), out var kind) ? kind : FileKind.Other;

    /// <summary>Total size and count per extension under <paramref name="root"/>, largest first.</summary>
    public static IReadOnlyList<FileTypeStats> ByExtension(FileNode root)
    {
        var totals = new Dictionary<string, (long Size, long Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in ScanResult.EnumerateNodes(root))
        {
            if (node.IsDirectory) continue;
            string extension = node.Extension;
            totals.TryGetValue(extension, out var total);
            totals[extension] = (total.Size + node.Size, total.Count + 1);
        }

        return totals
            .Select(pair => new FileTypeStats(pair.Key.Length == 0 ? "(no extension)" : pair.Key, KindOf(pair.Key), pair.Value.Size, pair.Value.Count))
            .OrderByDescending(stats => stats.Size)
            .ToList();
    }

    public static IReadOnlyList<FileKindStats> ByKind(IEnumerable<FileTypeStats> extensions) =>
        extensions
            .GroupBy(stats => stats.Kind)
            .Select(group => new FileKindStats(group.Key, group.Sum(s => s.Size), group.Sum(s => s.Count)))
            .OrderByDescending(stats => stats.Size)
            .ToList();

    private static Dictionary<string, FileKind> Build(Dictionary<FileKind, string> lists)
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, list) in lists)
        {
            foreach (string extension in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[extension] = kind;
        }
        return map;
    }
}
