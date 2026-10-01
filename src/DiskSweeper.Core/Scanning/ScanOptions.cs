namespace DiskSweeper.Core.Scanning;

public sealed class ScanOptions
{
    /// <summary>How many folders are read at the same time. SSDs benefit from more; spinning disks from fewer.</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 12);

    /// <summary>
    /// When running as administrator, switch on the read-only backup privilege so folders that even
    /// administrators normally can't open (other users' profiles, System Volume Information) get counted too.
    /// </summary>
    public bool UseBackupPrivilege { get; init; } = true;
}
