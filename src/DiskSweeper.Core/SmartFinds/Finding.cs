namespace DiskSweeper.Core.SmartFinds;

public enum FindCategory
{
    /// <summary>Caches, temp files, crash dumps: things programs recreate on their own.</summary>
    Junk,

    /// <summary>Game files no launcher knows about anymore.</summary>
    GameLeftover,

    /// <summary>Folders of programs that look uninstalled.</summary>
    ProgramLeftover,

    /// <summary>Saves and settings of games that don't seem to be installed.</summary>
    GameSaves,

    /// <summary>Old installers and disk images in Downloads.</summary>
    Downloads,

    /// <summary>Package caches for programming tools, re-downloaded when needed.</summary>
    Developer,

    /// <summary>Space Windows manages itself. Shown with the proper way to reclaim it, never deleted directly.</summary>
    WindowsManaged,
}

public enum Confidence
{
    /// <summary>Deleting this can't break anything; it gets recreated if needed.</summary>
    Safe,

    /// <summary>Very likely unneeded, but have a look first.</summary>
    Review,

    /// <summary>Use the Windows tool described in the advice instead of deleting.</summary>
    UseWindowsTool,
}

public enum FindingAction
{
    Delete,
    EmptyRecycleBin,
    AdviceOnly,
}

/// <summary>One thing Smart finds suggests cleaning, with how much it frees and why it's considered safe.</summary>
public sealed class Finding
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Path { get; init; }

    public required FindCategory Category { get; init; }

    public required Confidence Confidence { get; init; }

    /// <summary>Why this is listed, in plain words.</summary>
    public required string Explanation { get; init; }

    public FindingAction Action { get; init; } = FindingAction.Delete;

    /// <summary>Bytes that cleaning would free.</summary>
    public long Size { get; set; }

    /// <summary>Delete what's inside <see cref="Path"/> but keep the folder itself (for caches and Temp).</summary>
    public bool ContentsOnly { get; init; }

    /// <summary>Only touch items that haven't changed for this many days (so files in active use are left alone).</summary>
    public int MinAgeDays { get; init; }

    /// <summary>An explicit list of files to delete instead of <see cref="Path"/> (used for groups of downloads).</summary>
    public IReadOnlyList<string>? Files { get; init; }

    public bool NeedsAdmin { get; init; }

    public bool MayContainSaves { get; init; }

    /// <summary>For Windows-managed space: how to reclaim it properly.</summary>
    public string? Advice { get; init; }

    /// <summary>A command that does what <see cref="Advice"/> describes, if there is one.</summary>
    public string? AdviceCommand { get; init; }

    /// <summary>Newest change inside, when known. Old leftovers are more likely to be safe.</summary>
    public DateTime? LastChangedUtc { get; set; }

    /// <summary>The exact files and folders a clean-up would delete right now.</summary>
    public IReadOnlyList<string> ItemsToDelete()
    {
        if (Action != FindingAction.Delete) return [];
        if (Files is not null) return Files.Where(FileSystemWalk.Exists).ToList();
        if (!ContentsOnly) return FileSystemWalk.Exists(Path) ? [Path] : [];

        List<FileSystemWalk.Entry> entries;
        try
        {
            entries = FileSystemWalk.List(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var cutoff = DateTime.UtcNow.AddDays(-MinAgeDays);
        return entries.Where(e => MinAgeDays == 0 || e.LastWriteTimeUtc < cutoff).Select(e => e.Path).ToList();
    }

    public override string ToString() => $"{Title}: {Path} ({ByteSize.Format(Size)})";
}
