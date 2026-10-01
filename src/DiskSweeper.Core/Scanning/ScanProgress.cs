namespace DiskSweeper.Core.Scanning;

/// <summary>Live counters a UI can poll while a scan runs. Safe to read from any thread.</summary>
public sealed class ScanProgress
{
    private long _files;
    private long _folders;
    private long _bytes;
    private double _fraction = -1;

    public long FilesScanned => Interlocked.Read(ref _files);

    public long FoldersScanned => Interlocked.Read(ref _folders);

    public long BytesScanned => Interlocked.Read(ref _bytes);

    /// <summary>How far along the scan is (0 to 1) when that's known, as with a fast NTFS scan; otherwise null.</summary>
    public double? Fraction => Volatile.Read(ref _fraction) is var f && f >= 0 ? f : null;

    internal void SetFraction(double fraction) => Volatile.Write(ref _fraction, fraction);

    internal void Add(long files, long folders, long bytes)
    {
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _folders, folders);
        Interlocked.Add(ref _bytes, bytes);
    }
}
